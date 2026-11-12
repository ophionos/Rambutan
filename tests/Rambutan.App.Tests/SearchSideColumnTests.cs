using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Catalog;
using Rambutan.Core.Layout;
using Rambutan.Core.Models;
using SearchPage = Rambutan.App.Views.SearchPage;

namespace Rambutan.App.Tests;

/// <summary>Search's All tab on a pane with room for a side column: the rows
/// that play stay rows at the left, and the albums, artists and playlists are
/// shelves of cards at the right. A narrower pane keeps the one list.</summary>
public class SearchSideColumnTests
{
    private readonly FakeCatalog _catalog = new();

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
        [Song("a"), Playlist("e"), Album("b"), Song("c"), Artist("d"), Album("f"), Other("g")];

    private SearchPageViewModel Searched(IReadOnlyList<CatalogEntry> answer, bool sideRoom = false)
    {
        var vm = new SearchPageViewModel(_catalog, null!, null!) { HasSideRoom = sideRoom };
        Inline(() =>
        {
            vm.Query = "daft punk";
            vm.SearchCommand.Execute(null);
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

    private static string Describe(SearchSideShelf shelf) =>
        $"{shelf.Shelf.Title}: {string.Join(",", shelf.Shelf.Entries.Select(e => e.Title))}";

    [Fact]
    public void WithRoom_TheCardGroupsAreShelves_InTheAnswersOrder_AndTheRestStayRows()
    {
        var vm = Searched(Interleaved, sideRoom: true);

        Assert.Equal(["# Songs and videos", "a", "c", "# Other results", "g"], vm.Rows.Select(Describe));
        Assert.Equal(["Playlists: e", "Albums: b,f", "Artists: d"], vm.SideShelves.Select(Describe));
        Assert.True(vm.ShowsSideColumn);
        Assert.Equal(Interleaved, vm.Results);
    }

    [Fact]
    public void AShelfsSeeAll_IsItsKindsTab()
    {
        var vm = Searched(Interleaved, sideRoom: true);

        Assert.Equal(
            [SearchFilter.Playlists, SearchFilter.Albums, SearchFilter.Artists],
            vm.SideShelves.Select(s => s.SeeAll!.Filter!.Value));
    }

    [Fact]
    public void WithoutRoom_EveryGroupIsRows_AndThereAreNoShelves()
    {
        var vm = Searched(Interleaved);

        Assert.Equal(12, vm.Rows.Count);
        Assert.Empty(vm.SideShelves);
        Assert.False(vm.ShowsSideColumn);
    }

    [Fact]
    public void RoomComingAndGoing_MovesTheGroups_WithoutSearchingAgain()
    {
        var vm = Searched(Interleaved);

        vm.HasSideRoom = true;
        Assert.Equal(3, vm.SideShelves.Count);
        Assert.Equal(5, vm.Rows.Count);

        vm.HasSideRoom = false;
        Assert.Empty(vm.SideShelves);
        Assert.Equal(12, vm.Rows.Count);
        Assert.Single(_catalog.Searches);
    }

    [Fact]
    public void AnAnswerWithNoCardGroup_HasNoSideColumn()
    {
        var vm = Searched([Song("a"), Song("c")], sideRoom: true);

        Assert.Empty(vm.SideShelves);
        Assert.False(vm.ShowsSideColumn);
    }

    [Fact]
    public void ATypesTab_HasNoShelves()
    {
        var vm = Searched(Interleaved, sideRoom: true);

        Inline(() =>
        {
            vm.SelectedTab = vm.Tabs.Single(t => t.Filter == SearchFilter.Albums);
            _catalog.Searches[^1].Complete("Discovery", "Homework");
        });

        Assert.Equal(["Discovery", "Homework"], vm.Rows.Select(Describe));
        Assert.Empty(vm.SideShelves);
        Assert.False(vm.ShowsSideColumn);
    }

    [Fact]
    public void AFirstSearch_ShowsSkeletonsInBothColumns_UntilItsAnswer()
    {
        var vm = new SearchPageViewModel(_catalog, null!, null!) { HasSideRoom = true };
        Inline(() =>
        {
            vm.Query = "daft punk";
            vm.SearchCommand.Execute(null);

            Assert.True(vm.ShowsColumnSkeletons);
            Assert.True(vm.ShowsSideColumn);

            _catalog.Searches[^1].Result.TrySetResult([Song("a")]);
        });

        Assert.False(vm.ShowsColumnSkeletons);
        Assert.False(vm.ShowsSideColumn);
    }

    [Fact]
    public void WithoutRoom_ASearchShowsNoSkeletons()
    {
        var vm = new SearchPageViewModel(_catalog, null!, null!);
        Inline(() =>
        {
            vm.Query = "daft punk";
            vm.SearchCommand.Execute(null);
            Assert.False(vm.ShowsColumnSkeletons);
        });
    }

    [Fact]
    public void AFailedSearch_HasNoSideColumn_AndOneError()
    {
        var vm = Searched(Interleaved, sideRoom: true);

        Inline(() =>
        {
            vm.SearchCommand.Execute(null);
            _catalog.Searches[^1].Result.TrySetException(new HttpRequestException("offline"));
        });

        Assert.NotNull(vm.LoadError);
        Assert.Empty(vm.SideShelves);
        Assert.False(vm.ShowsSideColumn);
        Assert.False(vm.ShowsColumnSkeletons);
    }

    [Fact]
    public void ClearingTheBox_EmptiesTheShelves()
    {
        var vm = Searched(Interleaved, sideRoom: true);

        Inline(() => vm.Query = "");

        Assert.Empty(vm.SideShelves);
        Assert.False(vm.ShowsSideColumn);
    }

    private static T Settled<T>(T window) where T : Window
    {
        if (!window.IsVisible)
            window.Show();
        for (var i = 0; i < 4; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
        return window;
    }

    private (Window Window, SearchPage View, SearchPageViewModel Vm) Shown(double width)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var vm = Searched(Interleaved);
        var view = new SearchPage { DataContext = vm };
        var window = Settled(new Window { Width = width, Height = 1400, DataContext = new MainViewModel(), Content = view });
        return (window, view, vm);
    }

    private static double Left(Visual visual, Visual relativeTo) =>
        visual.TranslatePoint(default, relativeTo)!.Value.X;

    private static double Top(Visual visual, Visual relativeTo) =>
        visual.TranslatePoint(default, relativeTo)!.Value.Y;

    [Fact]
    public void AWidePane_PutsTheShelvesBesideTheRows_LevelWithThem() => Headless.Run(() =>
    {
        var (window, view, vm) = Shown(2600);

        Assert.True(vm.HasSideRoom);
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ResultsList");
        var shelves = view.GetVisualDescendants().OfType<ShelfView>().ToList();
        Assert.Equal(3, shelves.Count);
        Assert.All(shelves, s => Assert.True(s.IsEffectivelyVisible));

        var row = list.GetRealizedContainers().OfType<ListBoxItem>().First(c => c.DataContext is SongEntry);
        var rowRight = Left(row, view) + row.Bounds.Width;
        Assert.All(shelves, s => Assert.Equal(rowRight + SideColumnFit.Gap, Left(s, view), 1.0));
        Assert.Equal(Top(list, view), Top(shelves[0], view), 1.0);
        Assert.True(Top(shelves[0], view) < Top(shelves[1], view));
        // The shelf ends at the pane's edge, inside the scroller's padding.
        Assert.True(Left(shelves[0], view) + shelves[0].Bounds.Width <= 2600 - 16);
        Assert.DoesNotContain(list.Items, i => i is AlbumEntry or ArtistEntry or PlaylistEntry);
        window.Close();
    });

    [Fact]
    public void ANarrowerPane_KeepsOneList_AndNoShelf() => Headless.Run(() =>
    {
        var (window, view, vm) = Shown(SideColumnFit.MinPane(SearchPage.FieldMaxWidth) + 19);

        Assert.False(vm.HasSideRoom);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<ShelfView>(), s => s.IsEffectivelyVisible);
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ResultsList");
        Assert.Equal(12, list.ItemCount);
        var columns = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "Columns");
        Assert.Equal(0, columns.ColumnDefinitions[1].ActualWidth);
        window.Close();
    });

    [Fact]
    public void ThePaneAtTheThreshold_ShowsTheSideColumn() => Headless.Run(() =>
    {
        var (window, _, vm) = Shown(SideColumnFit.MinPane(SearchPage.FieldMaxWidth) + 20);

        Assert.True(vm.ShowsSideColumn);
        window.Close();
    });

    [Fact]
    public void NarrowingTheWindow_PutsTheGroupsBackInTheList() => Headless.Run(() =>
    {
        var (window, view, vm) = Shown(2600);
        Assert.True(vm.ShowsSideColumn);

        window.Width = 1200;
        Settled(window);

        Assert.False(vm.ShowsSideColumn);
        Assert.Equal(12, vm.Rows.Count);
        var columns = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "Columns");
        Assert.Equal(0, columns.ColumnDefinitions[1].ActualWidth);
        window.Close();
    });

    [Fact]
    public void Tab_GoesFromTheRows_ToEachShelfInTurn() => Headless.Run(() =>
    {
        var (window, view, vm) = Shown(2600);
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ResultsList");
        var first = vm.Rows.IndexOf(vm.Results[0]);
        list.SelectedIndex = first;
        list.ContainerFromIndex(first)!.Focus(NavigationMethod.Directional);

        // Each shelf: its See all, then its cards (one stop).
        var shelves = new List<string>();
        for (var i = 0; i < 12 && shelves.Count < 3; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Settled(window);
            if (window.FocusManager!.GetFocusedElement() is Visual focused &&
                focused.FindAncestorOfType<EntryCard>() is { } card &&
                card.FindAncestorOfType<ShelfView>() is { DataContext: Shelf shelf } &&
                !shelves.Contains(shelf.Title))
            {
                shelves.Add(shelf.Title);
            }
        }

        Assert.Equal(["Playlists", "Albums", "Artists"], shelves);
        window.Close();
    });
}
