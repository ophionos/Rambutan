using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Stats;

namespace Rambutan.App.ViewModels;

public enum ListeningStatsState { Loading, Ready, NothingYet, Unreadable, Newer }

/// <summary>A period the stats cover: the last <see cref="Days"/> days, ending today.</summary>
public sealed record StatsPeriodChoice(int Days, string Label, string PreviousLabel, string EmptyText);

/// <summary>One day's bar; <see cref="Name"/> is what a screen reader says.</summary>
public sealed record StatsBar(string Name, double Height, bool IsToday);

/// <summary>One weekday-and-hour cell; <see cref="Strength"/> is 0 to 1 of the busiest cell.</summary>
public sealed record StatsCell(string Name, double Strength);

public sealed record StatsRow(int Rank, string Title, string? Subtitle, string Detail, string? ThumbnailUrl)
{
    public string Name => Subtitle is null
        ? Strings.Format("stats.row.name", ("rank", Rank), ("title", Title), ("detail", Detail))
        : Strings.Format("stats.row.nameWithSubtitle", ("rank", Rank), ("title", Title), ("subtitle", Subtitle), ("detail", Detail));
}

/// <summary>
/// Library → Stats: listening stats from the play log, calculated each time the
/// view shows or the period changes, never kept up to date as songs play. The
/// log is read on its own worker and the stats calculated off the UI thread.
/// </summary>
public partial class ListeningStatsViewModel : ViewModelBase
{
    public const double BarAreaHeight = 120;

    private readonly Func<Task<PlayLogContents>> _read;
    private readonly Func<bool> _recording;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private int _loads;

    public IReadOnlyList<StatsPeriodChoice> Periods { get; } =
    [
        new(ListeningStats.WeekDays, Strings.Get("stats.period.week"), Strings.Get("stats.previous.week"), Strings.Get("stats.empty.week")),
        new(ListeningStats.MonthDays, Strings.Get("stats.period.month"), Strings.Get("stats.previous.month"), Strings.Get("stats.empty.month")),
        new(ListeningStats.YearDays, Strings.Get("stats.period.year"), Strings.Get("stats.previous.year"), Strings.Get("stats.empty.year")),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDense))]
    private StatsPeriodChoice _selectedPeriod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(IsNothingYet), nameof(IsUnreadable),
        nameof(IsNewer), nameof(IsPeriodEmpty), nameof(HasPlays))]
    private ListeningStatsState _state = ListeningStatsState.Loading;

    /// <summary>The play log is off: what shows is what it held before.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NothingYetHint))]
    private bool _isRecordingOff;

    public string NothingYetHint => Strings.Get(IsRecordingOff ? "stats.nothingYet.offHint" : "stats.nothingYet.hint");

    /// <summary>A year's bars are too narrow for gaps between them.</summary>
    public bool IsDense => SelectedPeriod.Days > ListeningStats.MonthDays;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPeriodEmpty), nameof(HasPlays))]
    private ListeningSummary? _summary;

    [ObservableProperty] private string _listenedText = "";
    [ObservableProperty] private string _comparisonText = "";
    [ObservableProperty] private string _playsText = "";
    [ObservableProperty] private string _currentStreakText = "";
    [ObservableProperty] private string _longestStreakText = "";
    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private string _newSongsTitle = "";
    [ObservableProperty] private IReadOnlyList<StatsBar> _days = [];
    [ObservableProperty] private StatsBar? _selectedDay;
    [ObservableProperty] private IReadOnlyList<string> _hourLabels = [];
    /// <summary>The heatmap's rows, from the culture's first day of the week.</summary>
    [ObservableProperty] private IReadOnlyList<string> _heatmapDays = [];

    /// <summary>Row by row: 24 hours of <see cref="HeatmapDays"/>' first day, then the next.</summary>
    [ObservableProperty] private IReadOnlyList<StatsCell> _heatmapCells = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCellText))]
    private StatsCell? _selectedCell;

    /// <summary>The hour selected in the heatmap, or how to pick one.</summary>
    public string SelectedCellText => SelectedCell?.Name ?? Strings.Get("stats.heatmap.hint");
    [ObservableProperty] private IReadOnlyList<StatsRow> _topSongs = [];
    [ObservableProperty] private IReadOnlyList<StatsRow> _topArtists = [];
    [ObservableProperty] private IReadOnlyList<StatsRow> _topAlbums = [];
    [ObservableProperty] private IReadOnlyList<StatsRow> _newSongs = [];

    public bool IsLoading => State == ListeningStatsState.Loading;
    public bool IsReady => State == ListeningStatsState.Ready;
    public bool IsNothingYet => State == ListeningStatsState.NothingYet;
    public bool IsUnreadable => State == ListeningStatsState.Unreadable;
    public bool IsNewer => State == ListeningStatsState.Newer;

    /// <summary>The log has plays, none of them in this period.</summary>
    public bool IsPeriodEmpty => IsReady && Summary is { Listened.Ticks: 0 };

    public bool HasPlays => IsReady && Summary is { Listened.Ticks: > 0 };

    /// <summary>Opens Settings, where the play log is turned on or cleared.</summary>
    public Action? OpenSettings { get; set; }

    // Design-time
    public ListeningStatsViewModel() : this(() => Task.FromResult(new PlayLogContents(PlayLogState.Readable, [])), () => true) { }

    public ListeningStatsViewModel(
        Func<Task<PlayLogContents>> read, Func<bool> recording, TimeProvider? clock = null, TimeZoneInfo? zone = null)
    {
        _read = read;
        _recording = recording;
        _clock = clock ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        _selectedPeriod = Periods[0];
    }

    partial void OnSelectedPeriodChanged(StatsPeriodChoice value) => _ = LoadAsync();

    [RelayCommand]
    private void GoToSettings() => OpenSettings?.Invoke();

    /// <summary>Reads the log and calculates the stats again. A load overtaken by a
    /// later one leaves the screen to it.</summary>
    public async Task LoadAsync()
    {
        var load = ++_loads;
        IsRecordingOff = !_recording();
        if (Summary is null)
            State = ListeningStatsState.Loading;

        var days = SelectedPeriod.Days;
        PlayLogContents contents;
        ListeningSummary? summary = null;
        try
        {
            contents = await _read();
            if (contents is { State: PlayLogState.Readable, Plays.Count: > 0 })
            {
                var plays = contents.Plays;
                var now = _clock.GetUtcNow();
                summary = await Task.Run(() => ListeningStats.Calculate(plays, days, now, _zone));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("plays", $"Couldn't read the play log for the stats: {ex.Message}");
            contents = new PlayLogContents(PlayLogState.Unreadable, []);
        }

        if (load != _loads)
            return;

        if (summary is null)
        {
            Summary = null;
            State = contents.State switch
            {
                PlayLogState.Newer => ListeningStatsState.Newer,
                PlayLogState.Unreadable => ListeningStatsState.Unreadable,
                _ => ListeningStatsState.NothingYet,
            };
            return;
        }

        Show(summary);
        State = ListeningStatsState.Ready;
    }

    private void Show(ListeningSummary summary)
    {
        Summary = summary;
        ListenedText = Duration(summary.Listened);
        ComparisonText = Comparison(summary.Listened, summary.PreviousListened);
        PlaysText = summary.Plays.ToString("N0", Strings.Culture);
        CurrentStreakText = Strings.Format("stats.streak.days", ("count", summary.CurrentStreak));
        LongestStreakText = Strings.Format("stats.streak.days", ("count", summary.LongestStreak));
        // A year's range starts in the year before; then both ends name their year.
        var withYear = summary.First.Year != summary.Last.Year;
        RangeText = Strings.Format("stats.range",
            ("first", DayLabel(summary.First, withYear)), ("last", DayLabel(summary.Last, withYear)));

        var busiestDay = summary.PerDay.Max();
        Days = [.. summary.PerDay.Select((listened, i) =>
        {
            var day = summary.First.AddDays(i);
            return new StatsBar(
                Strings.Format("stats.day.name", ("day", DayLabel(day)), ("listened", Duration(listened))),
                busiestDay > TimeSpan.Zero ? BarAreaHeight * listened.Ticks / busiestDay.Ticks : 0,
                day == summary.Last);
        })];
        SelectedDay = Days[^1];

        HourLabels = [Hour(0), Hour(6), Hour(12), Hour(18)];
        var busiestHour = summary.Heatmap.Max();
        var firstDay = (int)Strings.Culture.DateTimeFormat.FirstDayOfWeek;
        var weekdays = Enumerable.Range(0, 7).Select(offset => (DayOfWeek)((firstDay + offset) % 7)).ToList();
        HeatmapDays = [.. weekdays.Select(Strings.Culture.DateTimeFormat.GetAbbreviatedDayName)];
        HeatmapCells = [.. weekdays.SelectMany(weekday => Enumerable.Range(0, 24).Select(hour =>
        {
            var listened = summary.Heatmap[(int)weekday * 24 + hour];
            return new StatsCell(
                Strings.Format("stats.cell.name", ("day", Strings.Culture.DateTimeFormat.GetDayName(weekday)),
                    ("from", Hour(hour)), ("to", Hour((hour + 1) % 24)), ("listened", Duration(listened))),
                busiestHour > TimeSpan.Zero ? (double)listened.Ticks / busiestHour.Ticks : 0);
        }))];
        SelectedCell = null;

        TopSongs = [.. summary.TopSongs.Select((s, i) => SongRow(s, i + 1))];
        TopArtists = [.. summary.TopArtists.Select((a, i) =>
            new StatsRow(i + 1, a.Artist.Name, null, Detail(a.Plays, a.Listened), null))];
        TopAlbums = [.. summary.TopAlbums.Select((a, i) =>
            new StatsRow(i + 1, a.Title, a.Artist, Detail(a.Plays, a.Listened), a.ThumbnailUrl))];
        NewSongs = [.. summary.NewSongs.Select((s, i) => SongRow(s, i + 1))];
        NewSongsTitle = Strings.Format("stats.newSongs.title", ("count", summary.NewSongCount));
    }

    private static StatsRow SongRow(RankedSong song, int rank) => new(
        rank, song.Track.Title,
        song.Track.Artists.Count == 0 ? null : string.Join(", ", song.Track.Artists.Select(a => a.Name)),
        Detail(song.Plays, song.Listened), song.Track.ThumbnailUrl);

    private static string Detail(int plays, TimeSpan listened) =>
        Strings.Format("stats.row.detail", ("count", plays), ("listened", Duration(listened)));

    private string Comparison(TimeSpan listened, TimeSpan previous)
    {
        var before = SelectedPeriod.PreviousLabel;
        if (previous == TimeSpan.Zero)
            return Strings.Format("stats.compare.nothingBefore", ("before", before));
        var difference = listened - previous;
        if (Math.Abs(difference.TotalMinutes) < 1)
            return Strings.Format("stats.compare.same", ("before", before));
        return Strings.Format(difference > TimeSpan.Zero ? "stats.compare.more" : "stats.compare.less",
            ("difference", Duration(difference.Duration())), ("before", before));
    }

    /// <summary>Hours and minutes, rounded down to the minute.</summary>
    public static string Duration(TimeSpan time)
    {
        var minutes = (long)time.TotalMinutes;
        if (minutes < 1)
            return time > TimeSpan.Zero ? Strings.Get("stats.duration.underMinute") : Strings.Get("stats.duration.none");
        return minutes < 60
            ? Strings.Format("stats.duration.minutes", ("minutes", minutes))
            : Strings.Format("stats.duration.hours", ("hours", minutes / 60), ("minutes", minutes % 60));
    }

    private static string DayLabel(DateOnly day, bool withYear = false) =>
        day.ToString(withYear ? "ddd d MMM yyyy" : "ddd d MMM", Strings.Culture);

    private static string Hour(int hour) =>
        new DateTime(2000, 1, 1, hour, 0, 0).ToString("t", Strings.Culture);
}
