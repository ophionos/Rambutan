using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Core.Models;
using Rambutan.Core.Stations;

namespace Rambutan.App.ViewModels;

/// <summary>
/// One Last.fm station: a shelf on Home, or an artist's station opened
/// from the artist's page. It loads on its own, so Home's YouTube
/// Music shelves never wait for it, and it always shows a state: loading, the
/// tracks, nothing in the list, Last.fm not sharing it, or a failure with Try again.
///
/// The station is a fresh sample on every request, so the shelf keeps the rows
/// it loaded; a card plays the station from that track, in the shelf's order.
/// Tracks with nothing to play aren't on the shelf. The list last loaded is
/// saved, and shows first (<see cref="ShowAsync"/>); while a load fails, the
/// list on show says how old it is.
/// </summary>
public partial class StationShelfViewModel : ViewModelBase
{
    private readonly HomeStations _stations;
    private readonly Func<IReadOnlyList<TrackInfo>, int, Task> _play;
    private CancellationTokenSource? _load;

    /// <summary>The user's station this is; null for an artist's.</summary>
    public StationKind? Kind { get; }

    /// <summary>The artist whose station this is; null for the user's.</summary>
    public string? Artist { get; }

    public string Title => Artist is not null
        ? Strings.Format("station.artist.title", ("name", Artist))
        : Kind switch
        {
            StationKind.Mix => Strings.Get("home.station.mix"),
            StationKind.Recommended => Strings.Get("home.station.recommended"),
            _ => Strings.Get("home.station.library"),
        };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasNoTracks))]
    private bool _isLoading;

    /// <summary>The loaded cards, as the shelf control takes them; null until loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private Shelf? _shelf;

    /// <summary>Last.fm answered without this list (an unknown or private
    /// profile, or a response this version can't read).</summary>
    [ObservableProperty] private string? _unavailable;

    /// <summary>Last.fm or YouTube Music couldn't be reached.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SavedNotice))]
    private LoadError? _loadError;

    /// <summary>When the rows on show were loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SavedNotice), nameof(LoadedText), nameof(HasNoTracks))]
    private DateTimeOffset? _loadedAt;

    /// <summary>"From Last.fm, loaded …", on the station's page.</summary>
    public string? LoadedText => LoadedAt is { } at
        ? Strings.Format("station.page.from", ("date", at.ToLocalTime().ToString("g", Strings.Culture)))
        : null;

    /// <summary>"Showing the list from …": a load failed, and the list on show
    /// is the one loaded before.</summary>
    public string? SavedNotice => LoadError is not null && Shelf is not null && LoadedAt is { } at
        ? Strings.Format("home.station.savedCopy", ("date", at.ToLocalTime().ToString("g", Strings.Culture)))
        : null;

    /// <summary>Loaded, and nothing in it plays.</summary>
    public bool IsEmpty => !IsLoading && Shelf is { Entries.Count: 0 };

    /// <summary>The rows behind the cards, in order, including those with nothing to play.</summary>
    public IReadOnlyList<StationRow> Rows { get; private set; } = [];

    /// <summary>Every row, for the station's page (<see cref="StationPageViewModel"/>).
    /// The page lists the shelf's own rows, since another request would be
    /// another sample.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(CanPlay), nameof(CoverUrl), nameof(HasNoTracks))]
    [NotifyCanExecuteChangedFor(nameof(PlayAllCommand), nameof(ShuffleCommand))]
    private IReadOnlyList<StationRowItem> _items = [];

    public bool CanPlay => Items.Any(i => i.IsPlayable);

    /// <summary>Loaded, and Last.fm's list had no tracks at all; the page says so.</summary>
    public bool HasNoTracks => !IsLoading && LoadedAt is not null && Items.Count == 0;

    /// <summary>The page's art: the first track's (a station has none of its own).</summary>
    public string? CoverUrl => Items.Select(i => i.ThumbnailUrl).FirstOrDefault(u => u is not null);

    /// <summary>"25 tracks • 3 not on YouTube Music".</summary>
    public string Summary
    {
        get
        {
            var tracks = Strings.Format("station.page.tracks", ("count", Items.Count));
            var missing = Items.Count(i => !i.IsPlayable);
            return missing == 0 ? tracks : $"{tracks} • {Strings.Format("station.page.missing", ("count", missing))}";
        }
    }

    public StationShelfViewModel(StationKind kind, HomeStations stations, Func<IReadOnlyList<TrackInfo>, int, Task> play)
        : this(stations, play)
    {
        Kind = kind;
    }

    /// <summary>An artist's station. Nothing is loaded until <see cref="LoadAsync"/>.</summary>
    public StationShelfViewModel(string artist, HomeStations stations, Func<IReadOnlyList<TrackInfo>, int, Task> play)
        : this(stations, play)
    {
        Artist = artist;
    }

    private StationShelfViewModel(HomeStations stations, Func<IReadOnlyList<TrackInfo>, int, Task> play)
    {
        _stations = stations;
        _play = play;
        Strings.WhenChanged(this, static vm => vm.OnLanguageChanged());
    }

    /// <summary>The shelf comes on Home: shows the list saved last time, and
    /// loads a new one only when there is none or it is old.</summary>
    public Task ShowAsync()
    {
        if (Kind is not { } kind || _stations.Saved(kind) is not { } saved)
            return LoadAsync();
        Show(saved.Rows, saved.LoadedAt);
        return _stations.IsStale(saved) ? LoadAsync() : Task.CompletedTask;
    }

    /// <summary>Loads a fresh sample; one already in flight is dropped. The
    /// cards on show stay until the new ones arrive.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        _load?.Cancel();
        var load = _load = new CancellationTokenSource();
        IsLoading = true;
        LoadError = null;
        try
        {
            IReadOnlyList<StationRow> rows;
            DateTimeOffset loadedAt;
            if (Artist is { } artist)
                (rows, loadedAt) = await _stations.LoadArtistAsync(artist, load.Token);
            else
            {
                var snapshot = await _stations.LoadAsync(Kind!.Value, load.Token);
                (rows, loadedAt) = (snapshot.Rows, snapshot.LoadedAt);
            }
            if (load.IsCancellationRequested)
                return;
            Show(rows, loadedAt);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
        }
        catch (StationUnavailableException) when (!load.IsCancellationRequested)
        {
            Rows = [];
            Items = [];
            Shelf = null;
            LoadedAt = null;
            Unavailable = UnavailableText;
        }
        catch (Exception ex) when (!load.IsCancellationRequested)
        {
            // Cards already on show stay; the failure shows beside the title.
            var error = LoadError.From(ex, LoadSubject.Station, LoadCommand);
            // The generic offline title names YouTube Music; this list needs Last.fm too.
            LoadError = error.IsOffline ? error with { Title = Strings.Get("home.station.offline") } : error;
        }
        finally
        {
            if (_load == load)
                IsLoading = false;
        }
    }

    private string UnavailableText =>
        Strings.Get(Artist is null ? "home.station.unavailable" : "station.artist.unavailable");

    private void Show(IReadOnlyList<StationRow> rows, DateTimeOffset loadedAt)
    {
        Rows = rows;
        Items = rows.Select(r => new StationRowItem(r)).ToList();
        Unavailable = null;
        LoadedAt = loadedAt;
        Shelf = new Shelf(Title, rows
            .Where(r => r.Track is not null)
            .Select(r => (CatalogEntry)new SongEntry
            {
                Title = r.Track!.Title,
                Subtitle = string.Join(", ", r.Track.Artists.Select(a => a.Name)),
                ThumbnailUrl = r.Track.ThumbnailUrl,
                Track = r.Track,
            })
            .ToList());
        OnPropertyChanged(nameof(SavedNotice));
        OnPropertyChanged(nameof(LoadedText));
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SavedNotice));
        // The shelf control shows the title the Shelf was built with.
        if (Shelf is { } shelf)
            Shelf = shelf with { Title = Title };
        if (Unavailable is not null)
            Unavailable = UnavailableText;
        Items = Rows.Select(r => new StationRowItem(r)).ToList();
    }

    /// <summary>Stops a load in flight: the shelf left Home.</summary>
    public void Cancel() => _load?.Cancel();

    /// <summary>A card: the station plays from that track on, and the queue holds the
    /// rest of the shelf, so Next goes where the listener was looking.</summary>
    [RelayCommand]
    private Task PlayFromAsync(CatalogEntry entry)
    {
        if (Shelf is not { } shelf || entry is not SongEntry)
            return Task.CompletedTask;
        var songs = shelf.Entries.OfType<SongEntry>().ToList();
        var start = songs.FindIndex(e => ReferenceEquals(e, entry));
        return start < 0 ? Task.CompletedTask : _play(songs.Select(e => e.Track).ToList(), start);
    }

    private List<TrackInfo> Playable() => Items.Select(i => i.Track).OfType<TrackInfo>().ToList();

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private Task PlayAllAsync() => _play(Playable(), 0);

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private Task ShuffleAsync() => _play(EntryPlayback.Shuffled(Playable(), Random.Shared), 0);

    /// <summary>A row on the page: plays the station's playable rows from that
    /// one on. A row with nothing to play does nothing.</summary>
    [RelayCommand]
    private Task PlayRowAsync(StationRowItem? item)
    {
        if (item is not { Track: { } track })
            return Task.CompletedTask;
        var tracks = Playable();
        return _play(tracks, Math.Max(0, tracks.IndexOf(track)));
    }
}
