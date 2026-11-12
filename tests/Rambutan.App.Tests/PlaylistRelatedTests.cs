using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Layout;
using Rambutan.Core.Models;
using PlaylistPage = Rambutan.Core.Models.PlaylistPage;

namespace Rambutan.App.Tests;

/// <summary>The playlist page's related playlists: asked for only on a pane
/// with room for a column beside the rows, and shown there.</summary>
public class PlaylistRelatedTests
{
    private static readonly ContinuationToken TracksToken = new("tracks");
    private static readonly ContinuationToken RelatedToken = new("related");

    private static TrackInfo Track(int i) => new()
    {
        VideoId = $"v{i:D4}", Title = $"Song {i}", Artists = [new ArtistRef("Artist")],
    };

    private static Shelf RelatedShelf(int count = 4) => new(
        "Related playlists",
        Enumerable.Range(0, count)
            .Select(i => (CatalogEntry)new PlaylistEntry { Title = $"Related {i}", PlaylistId = $"RD{i}" })
            .ToList());

    private static PlaylistPage More(Shelf? related = null, params TrackInfo[] tracks) =>
        new() { PlaylistId = "PL1", Title = "", Tracks = tracks, Related = related };

    private sealed class Rig
    {
        public required FakeCatalog Catalog { get; init; }
        public required PlaylistPageViewModel Page { get; init; }
        public List<ContinuationToken> Asked { get; } = [];
    }

    private static Rig Loaded(
        ContinuationToken? tracks, ContinuationToken? related, Func<ContinuationToken, Task<PlaylistPage>>? more = null,
        DateTimeOffset? keptAt = null, int count = 30)
    {
        var catalog = new FakeCatalog
        {
            Playlist = new PlaylistPage
            {
                PlaylistId = "PL1", Title = "Road trip",
                Tracks = Enumerable.Range(0, count).Select(Track).ToList(),
                Continuation = tracks, RelatedContinuation = related, KeptAt = keptAt,
            },
        };
        var rig = new Rig
        {
            Catalog = catalog,
            Page = new PlaylistPageViewModel(catalog, null!, null!, null!, "PL1"),
        };
        more ??= token => Task.FromResult(token == RelatedToken ? More(RelatedShelf()) : More(null, Track(900)));
        catalog.PlaylistMore = token =>
        {
            rig.Asked.Add(token);
            return more(token);
        };
        rig.Page.LoadAsync().GetAwaiter().GetResult();
        return rig;
    }

    [Fact]
    public void WithoutRoom_TheRelatedPlaylistsAreNotAskedFor()
    {
        var rig = Loaded(TracksToken, RelatedToken);

        Assert.Empty(rig.Asked);
        Assert.Null(rig.Page.Related);
        Assert.False(rig.Page.ShowsSideColumn);
    }

    [Fact]
    public void WithRoom_TheyAreAskedForOnce_AndTheColumnShows()
    {
        var rig = Loaded(TracksToken, RelatedToken);

        rig.Page.HasSideRoom = true;
        rig.Page.HasSideRoom = false;
        rig.Page.HasSideRoom = true;

        Assert.Equal([RelatedToken], rig.Asked);
        Assert.Equal(4, rig.Page.Related!.Entries.Count);
        Assert.True(rig.Page.ShowsSideColumn);
        // The tracks' own paging is untouched.
        Assert.True(rig.Page.HasMore);
        Assert.Equal(30, rig.Page.Tracks.Count);
    }

    [Fact]
    public void RoomLost_HidesTheColumn_AndKeepsTheShelf()
    {
        var rig = Loaded(TracksToken, RelatedToken);
        rig.Page.HasSideRoom = true;

        rig.Page.HasSideRoom = false;

        Assert.False(rig.Page.ShowsSideColumn);
        Assert.NotNull(rig.Page.Related);
    }

    [Fact]
    public async Task APageLoadedWithRoom_AsksAfterItsTracks()
    {
        var rig = Loaded(TracksToken, RelatedToken);
        rig.Page.HasSideRoom = true;
        rig.Asked.Clear();

        await rig.Page.LoadAsync();

        Assert.Equal([RelatedToken], rig.Asked);
        Assert.True(rig.Page.ShowsSideColumn);
    }

    [Fact]
    public void AnAnswerWithoutRelatedPlaylists_LeavesNoColumn()
    {
        var rig = Loaded(TracksToken, RelatedToken, _ => Task.FromResult(More()));

        rig.Page.HasSideRoom = true;

        Assert.Null(rig.Page.Related);
        Assert.False(rig.Page.ShowsSideColumn);
        Assert.Null(rig.Page.LoadError);
    }

    [Fact]
    public void AFailedRequest_LeavesThePageAsItWas_WithoutAnError()
    {
        var rig = Loaded(TracksToken, RelatedToken,
            _ => Task.FromException<PlaylistPage>(new HttpRequestException("offline")));

        rig.Page.HasSideRoom = true;

        Assert.Null(rig.Page.Related);
        Assert.False(rig.Page.ShowsSideColumn);
        Assert.Null(rig.Page.LoadError);
        Assert.Equal(30, rig.Page.Tracks.Count);
        Assert.True(rig.Page.HasMore);
    }

    [Fact]
    public void AKeptCopy_HasNoColumn_AndAsksNothing()
    {
        var rig = Loaded(null, RelatedToken, keptAt: DateTimeOffset.UnixEpoch);

        rig.Page.HasSideRoom = true;

        Assert.Empty(rig.Asked);
        Assert.False(rig.Page.ShowsSideColumn);
    }

    [Fact]
    public void APlaylistWithNoToken_HasNoColumn()
    {
        var rig = Loaded(null, null);

        rig.Page.HasSideRoom = true;

        Assert.Empty(rig.Asked);
        Assert.False(rig.Page.ShowsSideColumn);
    }

    [Fact]
    public async Task AShortPlaylist_WhoseOnlyTokenIsTheRelatedOne_AsksOnce_AndItsPagingEnds()
    {
        var rig = Loaded(RelatedToken, RelatedToken);

        rig.Page.HasSideRoom = true;
        await rig.Page.LoadMoreAsync();

        Assert.Equal([RelatedToken], rig.Asked);
        Assert.False(rig.Page.HasMore);
        Assert.True(rig.Page.ShowsSideColumn);
    }

    [Fact]
    public async Task WithoutRoom_AShortPlaylistsLastPage_StillBringsTheShelf_ForWhenThereIsRoom()
    {
        var rig = Loaded(RelatedToken, RelatedToken);

        await rig.Page.LoadMoreAsync();
        Assert.False(rig.Page.ShowsSideColumn);
        rig.Page.HasSideRoom = true;

        Assert.Equal([RelatedToken], rig.Asked);
        Assert.True(rig.Page.ShowsSideColumn);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static (Window Window, Views.PlaylistPage View, Rig Rig) Show(double width, int count = 120)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var rig = Loaded(TracksToken, RelatedToken, count: count);
        var view = new Views.PlaylistPage { DataContext = rig.Page };
        var window = new Window { Width = width, Height = 900, DataContext = new MainViewModel(), Content = view };
        window.Show();
        Settle(window);
        return (window, view, rig);
    }

    private static double Left(Visual visual, Visual relativeTo) =>
        visual.TranslatePoint(default, relativeTo)!.Value.X;

    private static double Top(Visual visual, Visual relativeTo) =>
        visual.TranslatePoint(default, relativeTo)!.Value.Y;

    [Fact]
    public void AWidePane_ShowsTheShelf_PastTheRows_LevelWithTheFirstRow() => Headless.Run(() =>
    {
        var (window, view, rig) = Show(2600);

        Assert.True(rig.Page.ShowsSideColumn);
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TrackList");
        var shelf = view.GetVisualDescendants().OfType<ShelfView>().Single();
        Assert.True(shelf.IsEffectivelyVisible);

        var row = list.GetRealizedContainers().OfType<ListBoxItem>().First();
        Assert.Equal(Views.PlaylistPage.RowsMaxWidth, Left(row, view) + row.Bounds.Width, 1.0);
        Assert.Equal(Views.PlaylistPage.RowsMaxWidth + SideColumnFit.Gap, Left(shelf, view), 1.0);
        Assert.Equal(Top(list, view), Top(shelf, view), 1.0);
        Assert.True(Left(shelf, view) + shelf.Bounds.Width <= 2600 - 16 + 0.5);
        window.Close();
    });

    [Fact]
    public void ANarrowerPane_HasNoShelf_AndAsksNothing() => Headless.Run(() =>
    {
        var (window, view, rig) = Show(SideColumnFit.MinPane(Views.PlaylistPage.RowsMaxWidth) + 15);

        Assert.False(rig.Page.HasSideRoom);
        Assert.Empty(rig.Asked);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<ShelfView>(), s => s.IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public void ThePaneAtTheThreshold_ShowsTheShelf() => Headless.Run(() =>
    {
        var (window, _, rig) = Show(SideColumnFit.MinPane(Views.PlaylistPage.RowsMaxWidth) + 16);

        Assert.True(rig.Page.ShowsSideColumn);
        window.Close();
    });

    [Fact]
    public void TheShelf_StaysWhileTheRowsScroll_AndFollowsTheListsTopWhenTheHeroCollapses() => Headless.Run(() =>
    {
        var (window, view, _) = Show(2600);
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TrackList");
        var shelf = view.GetVisualDescendants().OfType<ShelfView>().Single();
        var hero = view.GetVisualDescendants().OfType<CollapsingHero>().Single();
        var before = Top(shelf, view);

        ((ScrollViewer)list.Scroll!).Offset = new Vector(0, 1200);
        Settle(window);

        Assert.True(hero.IsCollapsed);
        Assert.Equal(Top(list, view), Top(shelf, view), 1.0);
        Assert.True(Top(shelf, view) < before);
        window.Close();
    });

    [Fact]
    public void Tab_GoesFromTheRows_ToTheShelf() => Headless.Run(() =>
    {
        var (window, view, _) = Show(2600);
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TrackList");
        list.SelectedIndex = 0;
        list.ContainerFromIndex(0)!.Focus(NavigationMethod.Directional);

        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Settle(window);

        var focused = Assert.IsAssignableFrom<Visual>(window.FocusManager!.GetFocusedElement());
        Assert.NotNull(focused.FindAncestorOfType<EntryCard>());
        Assert.NotNull(focused.FindAncestorOfType<ShelfView>());
        window.Close();
    });
}
