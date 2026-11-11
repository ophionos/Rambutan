using Rambutan.App.Services;
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
    public void RootNavigation_SetsTheSectionAndKeepsThePageItLeftInTheHistory()
    {
        var nav = Create();
        var settings = new Page();

        nav.GoTo(new Page(), NavSection.Library);
        nav.OpenAlbum("MPREb_a");
        var album = nav.Current;
        nav.GoTo(settings, NavSection.Settings);

        Assert.Equal(NavSection.Settings, nav.Section);
        Assert.Same(settings, nav.Current);
        Assert.True(nav.CanGoBack);
        Assert.False(nav.CanGoForward);

        nav.Back();

        Assert.Same(album, nav.Current);
        Assert.Equal(NavSection.Library, nav.Section);
    }

    [Fact]
    public void ARootClickReturnsToTheArtistOnBack_AndForwardToTheRoot()
    {
        var nav = Create();
        var library = new Page();
        nav.GoTo(new Page(), NavSection.Home);
        nav.OpenAlbum("MPREb_a");
        nav.OpenArtist("UC_b");
        var artist = nav.Current;

        nav.GoTo(library, NavSection.Library);
        Assert.Equal(NavSection.Library, nav.Section);

        nav.Back();
        Assert.Same(artist, nav.Current);
        Assert.Equal(NavSection.Home, nav.Section);
        Assert.True(nav.CanGoForward);

        nav.Forward();
        Assert.Same(library, nav.Current);
        Assert.Equal(NavSection.Library, nav.Section);
    }

    [Fact]
    public void ClickingTheRootThatIsShowingAddsNoEntry()
    {
        var nav = Create();
        var home = new Page();
        nav.GoTo(home, NavSection.Home);
        nav.GoTo(new Page(), NavSection.Search);
        var library = new Page();
        nav.GoTo(library, NavSection.Library);

        nav.GoTo(library, NavSection.Library);
        nav.GoTo(library, NavSection.Library);

        nav.Back();
        Assert.IsType<Page>(nav.Current);
        Assert.Equal(NavSection.Search, nav.Section);
        nav.Back();
        Assert.Same(home, nav.Current);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void AnotherPageOfTheShowingRootTakesItsPlace()
    {
        var nav = Create();
        nav.GoTo(new Page(), NavSection.Home);
        nav.GoTo(new Page(), NavSection.Settings);
        var fresh = new Page();

        nav.GoTo(fresh, NavSection.Settings);

        Assert.Same(fresh, nav.Current);
        nav.Back();
        Assert.Equal(NavSection.Home, nav.Section);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void RootNavigation_DropsTheForwardHistory()
    {
        var nav = Create();
        nav.GoTo(new Page(), NavSection.Home);
        nav.OpenAlbum("MPREb_a");
        nav.Back();
        Assert.True(nav.CanGoForward);

        nav.GoTo(new Page(), NavSection.Search);

        Assert.False(nav.CanGoForward);
    }

    [Fact]
    public void ASignInChange_LeavesNoWayBackToAPageLoadedForTheOldAccount()
    {
        var nav = Create();
        nav.GoTo(new Page(), NavSection.Home);
        nav.OpenAlbum("MPREb_a");
        var settings = new Page();
        nav.GoTo(settings, NavSection.Settings);
        nav.Back();
        Assert.True(nav.CanGoBack);
        Assert.True(nav.CanGoForward);

        nav.OnSignInChanged();

        Assert.False(nav.CanGoBack);
        Assert.False(nav.CanGoForward);
        Assert.IsType<AlbumPageViewModel>(nav.Current);
    }

    [Fact]
    public void ALinkToASearchStartsFromNothing()
    {
        var nav = Create();
        nav.GoTo(new Page(), NavSection.Home);
        nav.OpenAlbum("MPREb_a");

        nav.GoSearch("ocean");

        Assert.IsType<SearchPageViewModel>(nav.Current);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public async Task TheStatsPageIsASidebarRootThatLoadsTheStatsWhenItOpens()
    {
        var loads = 0;
        var stats = new ListeningStatsViewModel(
            () => { loads++; return Task.FromResult(new PlayLogContents(PlayLogState.Readable, [])); }, () => true);
        var nav = new NavigationService(new FakeCatalog(), null!, new TrackActionsViewModel(), stats: stats);
        nav.GoTo(new Page(), NavSection.Home);

        nav.GoStats();

        Assert.Same(stats, nav.Current);
        Assert.Equal(NavSection.Stats, nav.Section);
        Assert.True(nav.CanGoBack);
        await WaitAsync(() => loads == 1 && stats.State == ListeningStatsState.NothingYet);

        nav.GoStats();
        await WaitAsync(() => loads == 2);
        nav.Back();
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void WithoutAPlayLog_TheStatsEntryOpensNothing()
    {
        var nav = Create();
        var home = new Page();
        nav.GoTo(home, NavSection.Home);

        nav.GoStats();

        Assert.Same(home, nav.Current);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
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
