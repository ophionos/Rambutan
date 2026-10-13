using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>Page history, the active sidebar section and the album link.</summary>
public class NavigationServiceTests
{
    private sealed class Page : ViewModelBase;

    private static NavigationService Create() =>
        new(new FakeCatalog(), null!, new TrackActionsViewModel());

    [Fact]
    public void RootNavigation_SetsTheSectionAndClearsHistory()
    {
        var nav = Create();
        var settings = new Page();

        nav.GoTo(new Page(), NavSection.Library);
        nav.OpenAlbum("MPREb_a");
        nav.GoTo(settings, NavSection.Settings);

        Assert.Equal(NavSection.Settings, nav.Section);
        Assert.Same(settings, nav.Current);
        Assert.False(nav.CanGoBack);
        Assert.False(nav.CanGoForward);
    }

    [Fact]
    public void DrillDownPages_KeepTheSectionTheyWereOpenedFrom()
    {
        var nav = Create();
        nav.GoTo(new Page(), NavSection.Search);

        nav.OpenAlbum("MPREb_a");
        nav.OpenArtist("UC_b");

        Assert.Equal(NavSection.Search, nav.Section);
        nav.Back();
        Assert.Equal(NavSection.Search, nav.Section);
    }

    [Fact]
    public void Back_ReturnsTheSamePageInstance_AndForwardUndoesIt()
    {
        var nav = Create();
        var root = new Page();
        nav.GoTo(root, NavSection.Home);
        nav.OpenAlbum("MPREb_a");
        var album = nav.Current;

        nav.Back();

        Assert.Same(root, nav.Current);
        Assert.False(nav.CanGoBack);
        Assert.True(nav.CanGoForward);

        nav.Forward();

        Assert.Same(album, nav.Current);
        Assert.True(nav.CanGoBack);
        Assert.False(nav.CanGoForward);
    }

    [Fact]
    public void OpeningAPage_DropsTheForwardHistory()
    {
        var nav = Create();
        nav.GoTo(new Page(), NavSection.Home);
        nav.OpenAlbum("MPREb_a");
        nav.Back();

        nav.OpenPlaylist("PL_c");

        Assert.False(nav.CanGoForward);
        Assert.IsType<PlaylistPageViewModel>(nav.Current);
    }

    [Fact]
    public void BackAndForward_WithNoHistory_DoNothing()
    {
        var nav = Create();
        var root = new Page();
        nav.GoTo(root, NavSection.Home);

        nav.Back();
        nav.Forward();

        Assert.Same(root, nav.Current);
    }

    [Fact]
    public void BackKeepsTheSavedScrollOffset_ButARootClickStartsAtTheTop()
    {
        var nav = Create();
        var home = new Page();
        nav.GoTo(home, NavSection.Home);
        home.SavedScrollOffset = 640; // what ScrollMemory writes as the view leaves
        nav.OpenAlbum("MPREb_a");

        nav.Back();
        Assert.Equal(640, home.SavedScrollOffset);

        nav.GoTo(new Page(), NavSection.Search);
        nav.GoTo(home, NavSection.Home);
        Assert.Equal(0, home.SavedScrollOffset);
    }

    [Fact]
    public void GoToAlbum_OpensTheTracksAlbum_AndIgnoresTracksWithoutOne()
    {
        var actions = new TrackActionsViewModel();
        var opened = new List<string>();
        actions.OpenAlbum = opened.Add;

        actions.GoToAlbumCommand.Execute(Track("MPREb_x"));
        actions.GoToAlbumCommand.Execute(Track(null));
        actions.GoToAlbumCommand.Execute(null);

        Assert.Equal(["MPREb_x"], opened);

        static TrackInfo Track(string? albumId) => new()
        {
            VideoId = "v", Title = "t", Artists = [], Album = "a", AlbumBrowseId = albumId,
        };
    }
}
