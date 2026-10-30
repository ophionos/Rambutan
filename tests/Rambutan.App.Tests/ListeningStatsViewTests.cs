using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Rambutan.App.Controls;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Stats;

namespace Rambutan.App.Tests;

/// <summary>
/// Library → Stats: a state for every way the log can be, the period's words and
/// the charts' names, and the heatmap moving by row with the arrow keys.
/// </summary>
public class ListeningStatsViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PlayRecord Play(DateTimeOffset at, double minutes, string title = "Song") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        StartedAt = at,
        Listened = TimeSpan.FromMinutes(minutes),
        VideoId = "aaaaaaaaaaa",
        Title = title,
        Artists = [new ArtistRef("Band", "UCband")],
        Duration = TimeSpan.FromMinutes(4),
    };

    private static ListeningStatsViewModel Stats(PlayLogContents contents, bool recording = true) =>
        new(() => Task.FromResult(contents), () => recording, new Clock(), TimeZoneInfo.Utc);

    private static ListeningStatsViewModel Stats(params PlayRecord[] plays) =>
        Stats(new PlayLogContents(PlayLogState.Readable, plays));

    [Theory]
    [InlineData(PlayLogState.Readable, ListeningStatsState.NothingYet)]
    [InlineData(PlayLogState.Newer, ListeningStatsState.Newer)]
    [InlineData(PlayLogState.Unreadable, ListeningStatsState.Unreadable)]
    public async Task Without_plays_to_show_the_state_says_why(PlayLogState log, ListeningStatsState shown)
    {
        var vm = Stats(new PlayLogContents(log, []));

        await vm.LoadAsync();

        Assert.Equal(shown, vm.State);
        Assert.False(vm.HasPlays);
    }

    [Fact]
    public async Task A_log_that_fails_to_read_shows_as_unreadable()
    {
        var vm = new ListeningStatsViewModel(
            () => Task.FromException<PlayLogContents>(new IOException("locked")), () => true, new Clock(), TimeZoneInfo.Utc);

        await vm.LoadAsync();

        Assert.Equal(ListeningStatsState.Unreadable, vm.State);
    }

    [Fact]
    public async Task With_the_log_off_the_page_says_so_and_how_to_turn_it_on()
    {
        var vm = Stats(new PlayLogContents(PlayLogState.Readable, []), recording: false);

        await vm.LoadAsync();

        Assert.True(vm.IsRecordingOff);
        Assert.Contains("Turn it on in Settings", vm.NothingYetHint);
    }

    [Fact]
    public async Task The_week_is_compared_with_the_week_before_in_words()
    {
        var vm = Stats(
            Play(Now.AddDays(-1), 90, "This week"),
            Play(Now.AddDays(-8), 30, "Week before"));

        await vm.LoadAsync();

        Assert.Equal(ListeningStatsState.Ready, vm.State);
        Assert.Equal("1 h 30 min", vm.ListenedText);
        Assert.Equal("1 h 0 min more than the 7 days before", vm.ComparisonText);
        Assert.Equal("1", vm.PlaysText);
        Assert.Equal("Sat 24 Oct – Fri 30 Oct", vm.RangeText);
        Assert.Equal(7, vm.Days.Count);
        Assert.Equal("Thu 29 Oct: 1 h 30 min", vm.Days[5].Name);
        Assert.Equal(ListeningStatsViewModel.BarAreaHeight, vm.Days[5].Height);
        Assert.Same(vm.Days[^1], vm.SelectedDay);
        Assert.Equal(7 * 24, vm.HeatmapCells.Count);
        Assert.Equal("1. This week, Band, 1 play · 1 h 30 min", vm.TopSongs[0].Name);
    }

    [Fact]
    public async Task A_period_with_nothing_in_it_says_so_and_another_period_shows_what_it_has()
    {
        var vm = Stats(Play(Now.AddDays(-20), 10));

        await vm.LoadAsync();
        Assert.True(vm.IsPeriodEmpty);
        Assert.Equal("Nothing was played in the last 7 days.", vm.SelectedPeriod.EmptyText);

        vm.SelectedPeriod = vm.Periods[1];
        await vm.LoadAsync();

        Assert.False(vm.IsPeriodEmpty);
        Assert.Equal(30, vm.Days.Count);
        Assert.False(vm.IsDense);
        vm.SelectedPeriod = vm.Periods[2];
        Assert.True(vm.IsDense);
        await vm.LoadAsync();
        Assert.Equal("Fri 31 Oct 2025 – Fri 30 Oct 2026", vm.RangeText);
    }

    [Theory]
    [InlineData(0, "0 min")]
    [InlineData(20, "Under a minute")]
    [InlineData(59 * 60 + 59, "59 min")]
    [InlineData(2 * 3600 + 5 * 60, "2 h 5 min")]
    public void Durations_read_in_hours_and_minutes(int seconds, string text) =>
        Assert.Equal(text, ListeningStatsViewModel.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void The_heatmap_moves_a_row_with_up_and_down() => Headless.Run(() =>
    {
        var list = new ListBox
        {
            ItemsSource = Enumerable.Range(0, 7 * 24).Select(i => $"cell {i}").ToList(),
            ItemsPanel = new FuncTemplate<Panel?>(() => new GridNavigationPanel { Columns = 24 }),
        };
        var window = new Window { Width = 800, Height = 400, Content = list };
        window.Show();
        window.UpdateLayout();

        list.SelectedIndex = 30;
        list.ContainerFromIndex(30)!.Focus(NavigationMethod.Directional);

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal(54, list.SelectedIndex);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal(6, list.SelectedIndex);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal(6, list.SelectedIndex);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(7, list.SelectedIndex);

        // The end of a row is the end: Right doesn't wrap into the next day.
        list.SelectedIndex = 23;
        list.ContainerFromIndex(23)!.Focus(NavigationMethod.Directional);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(23, list.SelectedIndex);
        window.Close();
    });

    [Fact]
    public void The_cells_fill_the_width_exactly() => Headless.Run(() =>
    {
        var panel = new GridNavigationPanel { Columns = 24, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        for (var i = 0; i < 48; i++)
            panel.Children.Add(new Border { MinWidth = 40, Height = 20 });
        var window = new Window { Width = 1097, Height = 200, Content = panel };
        window.Show();
        window.UpdateLayout();

        Assert.Equal(0, panel.Children[0].Bounds.Left);
        Assert.Equal(1097, panel.Children[23].Bounds.Right);
        Assert.Equal(20, panel.Children[24].Bounds.Top);
        Assert.All(panel.Children, c => Assert.InRange(c.Bounds.Width, 45, 46));
        window.Close();
    });

    [Fact]
    public void Library_offers_the_stats_beside_its_sources()
    {
        var library = new LibraryPageViewModel(null!, null!, null!, null!, stats: Stats());

        Assert.Equal([LibrarySource.YouTubeMusic, LibrarySource.Stats], library.Sources);
        Assert.True(library.HasSources);

        library.Source = LibrarySource.Stats;
        Assert.True(library.IsStats);
        Assert.False(library.IsYouTubeMusic);
    }
}
