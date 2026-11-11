using System.Net.Http;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Services.Demo;
using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.InnerTube;

namespace Rambutan.App.Tests;

/// <summary>Search's All tab: the results under a heading per kind, each heading
/// with its tab's See all, in one list the arrow keys move through.</summary>
public class SearchGroupingTests
{
    private readonly FakeCatalog _catalog = new();

    /// <summary>No SynchronizationContext, so completing a fake runs the view
    /// model's continuation inline (see <see cref="SearchPageViewModelTests"/>).</summary>
    private static void Inline(Action body)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            body();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static SongEntry Song(string title) =>
        new() { Title = title, Track = new TrackInfo { VideoId = title, Title = title, Artists = [] } };

    private static AlbumEntry Album(string title) => new() { Title = title, BrowseId = title };

    private static ArtistEntry Artist(string title) => new() { Title = title, BrowseId = title };

    private static PlaylistEntry Playlist(string title) => new() { Title = title, PlaylistId = title };

    private static HistoryEntry Other(string title) =>
        new() { Title = title, Played = "Today", Track = new TrackInfo { VideoId = title, Title = title, Artists = [] } };

    private static readonly CatalogEntry[] Interleaved =
        [Song("a"), Album("b"), Song("c"), Artist("d"), Playlist("e"), Album("f"), Other("g")];

    private SearchPageViewModel Searched(IReadOnlyList<CatalogEntry> answer, TopResult? top = null)
    {
        var vm = new SearchPageViewModel(_catalog, null!, null!);
        Inline(() =>
        {
            vm.Query = "daft punk";
            vm.SearchCommand.Execute(null);
            _catalog.Searches[^1].Top = top;
            _catalog.Searches[^1].Result.TrySetResult(answer);
        });
        return vm;
    }

    private static string Describe(object row) => row switch
    {
        SearchResultHeading heading => $"# {heading.Title}",
        CatalogEntry entry => entry.Title,
        _ => row.GetType().Name,
    };

    [Fact]
    public void AllResults_GroupByKind_InTheOrderKindsFirstCome_KeepingEachKindsOrder()
    {
        var vm = Searched(Interleaved);

        Assert.Equal(
            ["# Songs and videos", "a", "c", "# Albums", "b", "f", "# Artists", "d", "# Playlists", "e", "# Other results", "g"],
            vm.Rows.Select(Describe));
        // Results stays the answer as sent: the queue and paging read it.
        Assert.Equal(Interleaved, vm.Results);
    }

    [Fact]
    public async Task TheRecordedAllSearch_IsGroupedWithoutLosingARow()
    {
        var client = new InnerTubeClient(new HttpClient(new DemoInnerTubeHandler(
            new DemoOptions(false, FindFixtures(), TimeSpan.Zero, new HashSet<string>()))));
        var overview = await client.SearchAllAsync("anything");
        var kinds = overview.Results.Select(e => e.GetType()).Distinct().ToList();
        Assert.True(kinds.Count >= 2, "the recorded answer has one kind only; the grouping would show nothing");

        var vm = Searched(overview.Results, overview.TopResult);

        // Stable grouping: the kinds by first appearance, each kind's rows in order.
        var expected = new List<string>();
        foreach (var kind in kinds)
        {
            var rows = overview.Results.Where(e => e.GetType() == kind).ToList();
            var heading = vm.Rows.OfType<SearchResultHeading>().Single(h =>
                vm.Rows.IndexOf(h) + 1 < vm.Rows.Count && ReferenceEquals(vm.Rows[vm.Rows.IndexOf(h) + 1], rows[0]));
            expected.Add($"# {heading.Title}");
            expected.AddRange(rows.Select(r => r.Title));
        }
        Assert.Equal(expected, vm.Rows.Select(Describe));
        Assert.Equal(overview.Results.Count, vm.Rows.OfType<CatalogEntry>().Count());
        Assert.Equal(kinds.Count, vm.Rows.OfType<SearchResultHeading>().Count());
        Assert.NotNull(vm.TopResult);
    }

    [Fact]
    public void SeeAll_IsOnAlbumsArtistsAndPlaylists_AndSwitchesToThatTab()
    {
        var vm = Searched(Interleaved);
        var headings = vm.Rows.OfType<SearchResultHeading>().ToDictionary(h => h.Group);

        Assert.Null(headings[SearchResultGroup.SongsAndVideos].SeeAll);
        Assert.Null(headings[SearchResultGroup.Other].SeeAll);
        Assert.Equal(SearchFilter.Albums, headings[SearchResultGroup.Albums].SeeAll?.Filter);
        Assert.Equal(SearchFilter.Artists, headings[SearchResultGroup.Artists].SeeAll?.Filter);
        Assert.Equal(SearchFilter.Playlists, headings[SearchResultGroup.Playlists].SeeAll?.Filter);

        Inline(() =>
        {
            vm.ShowTabCommand.Execute(headings[SearchResultGroup.Artists].SeeAll);
            Assert.Same(vm.Tabs.Single(t => t.Filter == SearchFilter.Artists), vm.SelectedTab);
            var search = _catalog.Searches[^1];
            Assert.Equal(SearchFilter.Artists, search.Filter);
            Assert.Equal("daft punk", search.Query);
            search.Complete("Daft Punk", "Thomas Bangalter");
        });

        // A type's tab is not grouped.
        Assert.Equal(["Daft Punk", "Thomas Bangalter"], vm.Rows.Select(Describe));
    }

    [Fact]
    public void ATypesNextPage_AppendsToItsRows()
    {
        var vm = new SearchPageViewModel(_catalog, null!, null!);
        Inline(() =>
        {
            vm.Query = "daft punk";
            vm.SelectedTab = vm.Tabs.Single(t => t.Filter == SearchFilter.Albums);
            _catalog.Searches[0].CompleteWithMore("next", "Discovery");
            var first = vm.Rows[0];

            vm.LoadMoreCommand.Execute(null);
            _catalog.Searches[1].Complete("Homework");

            Assert.Equal(["Discovery", "Homework"], vm.Rows.Select(Describe));
            Assert.Same(first, vm.Rows[0]);
        });
    }

    [Fact]
    public async Task OpeningAHeading_DoesNothing()
    {
        var vm = Searched(Interleaved);
        var heading = vm.Rows.OfType<SearchResultHeading>().First();

        Assert.True(vm.OpenCommand.CanExecute(heading));
        await vm.OpenCommand.ExecuteAsync(heading);
    }

    [Fact]
    public void ClearingTheBox_EmptiesTheRows()
    {
        var vm = Searched(Interleaved);
        Assert.NotEmpty(vm.Rows);

        Inline(() => vm.Query = "");

        Assert.Empty(vm.Rows);
    }

    private static T Settled<T>(T window) where T : Window
    {
        if (!window.IsVisible)
            window.Show();
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
        return window;
    }

    private static ListBox ResultsList(Visual view) =>
        view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ResultsList");

    [Fact]
    public void TheList_SpeaksHeadingsAsHeadings_AndSeeAllNamesItsGroup() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var vm = Searched(Interleaved);
        var view = new Rambutan.App.Views.SearchPage { DataContext = vm };
        var window = Settled(new Window { Width = 1000, Height = 1400, DataContext = new MainViewModel(), Content = view });

        var list = ResultsList(view);
        var headings = list.GetRealizedContainers().OfType<ListBoxItem>()
            .Where(c => c.DataContext is SearchResultHeading).ToList();
        Assert.Equal(5, headings.Count);
        Assert.All(headings, h =>
        {
            Assert.False(h.Focusable);
            Assert.Equal(2, AutomationProperties.GetHeadingLevel(h));
        });
        Assert.Equal("Albums", AutomationProperties.GetName(headings[1]));

        var seeAll = list.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("seeall") && b.IsEffectivelyVisible).ToList();
        Assert.Equal(["See all: Albums", "See all: Artists", "See all: Playlists"],
            seeAll.Select(AutomationProperties.GetName));
        Assert.All(seeAll, b => Assert.False(b.IsTabStop));

        Inline(() => seeAll[0].Command!.Execute(seeAll[0].CommandParameter));
        Assert.Equal(SearchFilter.Albums, vm.SelectedTab.Filter);
        window.Close();
    });

    [Fact]
    public void ArrowDown_FromAGroupsLastRow_ReachesTheNextGroupsFirstRow() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var vm = Searched(Interleaved);
        var view = new Rambutan.App.Views.SearchPage { DataContext = vm };
        var window = Settled(new Window { Width = 1000, Height = 1400, DataContext = new MainViewModel(), Content = view });

        var list = ResultsList(view);
        // "c" ends the songs; "b" starts the albums.
        var last = vm.Rows.IndexOf(vm.Results[2]);
        list.SelectedIndex = last;
        list.ContainerFromIndex(last)!.Focus(NavigationMethod.Directional);

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Settled(window);

        Assert.Same(vm.Results[1], list.SelectedItem);
        Assert.Same(list.ContainerFromItem(vm.Results[1]), window.FocusManager!.GetFocusedElement());

        // And back up over the heading.
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Settled(window);
        Assert.Same(vm.Results[2], list.SelectedItem);
        window.Close();
    });

    private static string FindFixtures()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Rambutan.InnerTube.Tests", "Fixtures");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("InnerTube fixtures not found above the test binaries.");
    }
}
