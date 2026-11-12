using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Layout;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;
using AlbumPage = Rambutan.Core.Models.AlbumPage;
using ArtistPage = Rambutan.Core.Models.ArtistPage;

namespace Rambutan.App.Tests;

/// <summary>The album page's side column: more by the album's artist, asked
/// for only on a pane with room for a column beside the rows.</summary>
public class AlbumArtistColumnTests
{
    private static TrackInfo Track(string id) => new()
    {
        VideoId = id, Title = $"Song {id}", Artists = [new ArtistRef("Daft Punk", "UCdp")],
    };

    private static AlbumEntry Album(string id) => new() { Title = id, BrowseId = id };

    private static ArtistPage Artist() => new()
    {
        BrowseId = "UCdp", Name = "Daft Punk",
        TopSongs = [Track("top1"), Track("top2")],
        Shelves =
        [
            new Shelf("Videos", [Album("V1")]),
            new Shelf("Albums", [Album("MPRE1"), Album("MPRE2")]),
            new Shelf("Fans might also like", [new ArtistEntry { Title = "Justice", BrowseId = "UCj" }]),
        ],
    };

    private static FakeCatalog Catalog(string? artistId = "UCdp", DateTimeOffset? keptAt = null) => new()
    {
        Album = new AlbumPage
        {
            BrowseId = "MPRE1", Title = "Discovery", Artist = "Daft Punk", ArtistBrowseId = artistId,
            Tracks = [Track("a"), Track("b"), Track("c")], KeptAt = keptAt,
        },
        Artist = Artist(),
    };

    private static async Task<AlbumPageViewModel> LoadedAsync(FakeCatalog catalog, QueueModel? queue = null)
    {
        var page = new AlbumPageViewModel(
            catalog, queue is null ? null! : FakePlayback.Coordinator(queue), "MPRE1");
        await page.LoadAsync();
        return page;
    }

    [Fact]
    public async Task WithoutRoom_TheArtistIsNotAskedFor()
    {
        var catalog = Catalog();

        var page = await LoadedAsync(catalog);

        Assert.Empty(catalog.ArtistAsked);
        Assert.False(page.ShowsSideColumn);
    }

    [Fact]
    public async Task WithRoom_TheArtistIsAskedForOnce_AndTheColumnShows()
    {
        var catalog = Catalog();
        var page = await LoadedAsync(catalog);

        page.HasSideRoom = true;
        page.HasSideRoom = false;
        page.HasSideRoom = true;

        Assert.Equal(["UCdp"], catalog.ArtistAsked);
        Assert.True(page.ShowsSideColumn);
        Assert.Equal(["top1", "top2"], page.ArtistSongs.Select(t => t.VideoId));
        Assert.Equal(["Albums", "Fans might also like"], page.ArtistShelves.Select(s => s.Title));
        // The album on the page is left out of its artist's albums.
        Assert.Equal(["MPRE2"], page.ArtistShelves[0].Entries.Cast<AlbumEntry>().Select(a => a.BrowseId));
        Assert.Equal(Strings.Format("album.topSongsBy", ("artist", "Daft Punk")), page.ArtistSongsTitle);
    }

    [Fact]
    public async Task RoomLost_HidesTheColumn_AndKeepsWhatWasLoaded()
    {
        var page = await LoadedAsync(Catalog());
        page.HasSideRoom = true;

        page.HasSideRoom = false;

        Assert.False(page.ShowsSideColumn);
        Assert.Equal(2, page.ArtistSongs.Count);
    }

    [Fact]
    public async Task APageLoadedWithRoom_AsksAfterItsTracks()
    {
        var catalog = Catalog();
        var gate = new TaskCompletionSource();
        catalog.AlbumGate = gate.Task;
        var page = new AlbumPageViewModel(catalog, null!, "MPRE1") { HasSideRoom = true };

        var load = page.LoadAsync();
        Assert.Empty(catalog.ArtistAsked);
        gate.SetResult();
        await load;

        Assert.Equal(["UCdp"], catalog.ArtistAsked);
        Assert.True(page.ShowsSideColumn);
    }

    [Fact]
    public async Task WhileTheArtistLoads_TheColumnShowsSkeletons()
    {
        var catalog = Catalog();
        var gate = new TaskCompletionSource();
        catalog.ArtistGate = gate.Task;
        var page = await LoadedAsync(catalog);

        page.HasSideRoom = true;

        Assert.True(page.IsLoadingArtist);
        Assert.True(page.ShowsSideColumn);

        gate.SetResult();
        await Task.Yield();
        Assert.False(page.IsLoadingArtist);
        Assert.Equal(2, page.ArtistSongs.Count);
    }

    [Fact]
    public async Task LeavingWhileTheArtistLoads_CancelsIt_AndComingBackAsksAgain()
    {
        var catalog = Catalog();
        catalog.ArtistGate = new TaskCompletionSource().Task;
        var page = await LoadedAsync(catalog);
        page.HasSideRoom = true;

        page.HasSideRoom = false;

        Assert.False(page.IsLoadingArtist);
        Assert.Null(page.ArtistError);
        Assert.Empty(page.ArtistSongs);

        catalog.ArtistGate = null;
        page.HasSideRoom = true;

        Assert.Equal(["UCdp", "UCdp"], catalog.ArtistAsked);
        Assert.True(page.ShowsSideColumn);
        Assert.Equal(2, page.ArtistSongs.Count);
    }

    [Fact]
    public async Task AFailure_ShowsInTheColumn_NotOverTheTracks_AndTryAgainLoads()
    {
        var catalog = Catalog();
        catalog.Artist = null;
        var page = await LoadedAsync(catalog);

        page.HasSideRoom = true;

        Assert.NotNull(page.ArtistError);
        Assert.Null(page.LoadError);
        Assert.True(page.ShowsSideColumn);
        Assert.Equal(3, page.Tracks.Count);

        catalog.Artist = Artist();
        page.ArtistError!.Retry!.Execute(null);

        Assert.Null(page.ArtistError);
        Assert.Equal(2, page.ArtistSongs.Count);
        Assert.True(page.ShowsSideColumn);
    }

    [Fact]
    public async Task ADismissedFailure_TakesTheColumnAway_AndIsNotAskedAgain()
    {
        var catalog = Catalog();
        catalog.Artist = null;
        var page = await LoadedAsync(catalog);
        page.HasSideRoom = true;

        page.DismissArtistErrorCommand.Execute(null);
        page.HasSideRoom = false;
        page.HasSideRoom = true;

        Assert.Null(page.ArtistError);
        Assert.False(page.ShowsSideColumn);
        Assert.Single(catalog.ArtistAsked);
    }

    [Fact]
    public async Task AnAlbumWithoutAnArtistLink_HasNoColumn()
    {
        var catalog = Catalog(artistId: null);
        var page = await LoadedAsync(catalog);

        page.HasSideRoom = true;

        Assert.Empty(catalog.ArtistAsked);
        Assert.False(page.ShowsSideColumn);
    }

    [Fact]
    public async Task TheCopyKeptWithTheDownloads_HasNoColumn()
    {
        var catalog = Catalog(keptAt: DateTimeOffset.UtcNow);
        var page = await LoadedAsync(catalog);

        page.HasSideRoom = true;

        Assert.Empty(catalog.ArtistAsked);
        Assert.False(page.ShowsSideColumn);
    }

    [Fact]
    public async Task AnArtistWithNoneOfTheShelves_LeavesNoColumn()
    {
        var catalog = Catalog();
        catalog.Artist = Artist() with { TopSongs = [], Shelves = [new Shelf("Videos", [Album("V1")])] };
        var page = await LoadedAsync(catalog);

        page.HasSideRoom = true;

        Assert.Single(catalog.ArtistAsked);
        Assert.False(page.ShowsSideColumn);
        Assert.Null(page.ArtistError);
    }

    [Fact]
    public async Task ReloadingThePage_AsksForTheArtistAgain()
    {
        var catalog = Catalog();
        var page = await LoadedAsync(catalog);
        page.HasSideRoom = true;

        await page.LoadAsync();

        Assert.Equal(["UCdp", "UCdp"], catalog.ArtistAsked);
        Assert.Equal(2, page.ArtistSongs.Count);
        Assert.Equal(2, page.ArtistShelves.Count);
    }

    [Fact]
    public async Task ATopSongRow_PlaysTheTopSongsFromThatRow()
    {
        var queue = new QueueModel();
        var page = await LoadedAsync(Catalog(), queue);
        page.HasSideRoom = true;

        await page.PlayArtistSongFromCommand.ExecuteAsync(page.ArtistSongs[1]);

        Assert.Equal(["top1", "top2"], queue.Items.Select(t => t.VideoId));
        Assert.Equal("top2", queue.Current?.VideoId);
    }

    // ---- The view ----

    private sealed record Shown(Window Window, Views.AlbumPage View, AlbumPageViewModel Page, FakeCatalog Catalog);

    /// <summary>The page in a window, loaded: the fake catalog answers at once.</summary>
    private static Shown Open(double width, Task? artistGate = null)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var catalog = Catalog();
        catalog.ArtistGate = artistGate;
        var page = new AlbumPageViewModel(catalog, null!, "MPRE1");
        var view = new Views.AlbumPage { DataContext = page };
        var window = new Window { Width = width, Height = 800, DataContext = new MainViewModel(), Content = view };
        window.Show();
        window.UpdateLayout();
        page.LoadAsync().GetAwaiter().GetResult();
        window.UpdateLayout();
        return new Shown(window, view, page, catalog);
    }

    private static ScrollViewer Column(Views.AlbumPage view) => view.FindControl<ScrollViewer>("SideColumn")!;

    [Fact]
    public void ANarrowPane_KeepsTheRowsAlone() => Headless.Run(() =>
    {
        var shown = Open(SideColumnFit.MinPane(Views.AlbumPage.RowsMaxWidth));

        Assert.False(shown.Page.HasSideRoom);
        Assert.Empty(shown.Catalog.ArtistAsked);
        Assert.False(Column(shown.View).IsVisible);
        shown.Window.Close();
    });

    [Fact]
    public void AWidePane_PutsTheColumnPastTheRows_LevelWithTheFirstRow() => Headless.Run(() =>
    {
        var shown = Open(2400);

        var column = Column(shown.View);
        Assert.True(column.IsVisible);
        var list = shown.View.FindControl<ListBox>("TrackList")!;
        var at = column.TranslatePoint(default, shown.View)!.Value;
        Assert.Equal(Views.AlbumPage.RowsMaxWidth + SideColumnFit.Gap, at.X);
        Assert.Equal(list.TranslatePoint(default, shown.View)!.Value.Y, at.Y);
        Assert.True(at.X + column.Bounds.Width <= shown.View.Bounds.Width);

        // No track row reaches under the column.
        var row = list.GetVisualDescendants().OfType<ListBoxItem>().First();
        Assert.True(row.TranslatePoint(default, shown.View)!.Value.X + row.Bounds.Width <= at.X);

        var rows = column.GetVisualDescendants().OfType<ItemsControl>()
            .Single(i => AutomationProperties.GetName(i) == Strings.Format("album.topSongsBy", ("artist", "Daft Punk")));
        Assert.Equal(2, rows.ItemCount);
        Assert.Equal(2, column.GetVisualDescendants().OfType<ShelfView>().Count());
        shown.Window.Close();
    });

    [Fact]
    public void NarrowingTheWindow_TakesTheColumnAway_AndWideningBringsItBack() => Headless.Run(() =>
    {
        var shown = Open(2400);

        shown.View.Width = 1200;
        shown.Window.UpdateLayout();
        Assert.False(Column(shown.View).IsVisible);

        shown.View.Width = 2400;
        shown.Window.UpdateLayout();
        Assert.True(Column(shown.View).IsVisible);
        Assert.Single(shown.Catalog.ArtistAsked);
        shown.Window.Close();
    });

    [Fact]
    public void LeavingThePage_CancelsTheArtistRequest() => Headless.Run(() =>
    {
        var shown = Open(2400, artistGate: new TaskCompletionSource().Task);
        Assert.True(shown.Page.IsLoadingArtist);

        shown.Window.Content = null;
        shown.Window.UpdateLayout();

        Assert.False(shown.Page.IsLoadingArtist);
        Assert.False(shown.Page.HasSideRoom);
        shown.Window.Close();
    });

    [Fact]
    public void Tab_GoesFromTheTracksToTheColumn() => Headless.Run(() =>
    {
        var shown = Open(2400);
        var list = shown.View.FindControl<ListBox>("TrackList")!;
        var column = Column(shown.View);

        list.ContainerFromIndex(0)!.Focus();
        shown.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);

        var focused = shown.Window.FocusManager!.GetFocusedElement() as Control;
        Assert.NotNull(focused);
        Assert.Contains(column, focused.GetVisualAncestors());
        shown.Window.Close();
    });
}
