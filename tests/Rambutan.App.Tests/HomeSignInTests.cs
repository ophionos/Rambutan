using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>
/// Home after the account changes: its feed was the old account's, so it loads
/// again, at once when it is on screen and on the next visit when it isn't.
/// The Library on screen loads again where it is.
/// </summary>
public class HomeSignInTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly NavigationService _navigation;

    public HomeSignInTests() => _navigation = new NavigationService(_catalog, null!, null!);

    private HomePageViewModel LoadedHome()
    {
        _navigation.GoHome();
        _catalog.HomeCalls[0].Complete(null, "Quick picks");
        return Assert.IsType<HomePageViewModel>(_navigation.Current);
    }

    [Fact]
    public void OnScreen_LoadsAgain_KeepingTheOldShelvesUntilTheNewOnesCome()
    {
        var home = LoadedHome();

        _navigation.OnSignInChanged();

        Assert.Equal(2, _catalog.HomeCalls.Count);
        Assert.Equal("Quick picks", Assert.Single(home.Shelves).Title);
        _catalog.HomeCalls[1].Complete(null, "Your mixes");
        Assert.Equal("Your mixes", Assert.Single(home.Shelves).Title);
    }

    [Fact]
    public void OffScreen_LoadsOnTheNextVisit_NotBefore()
    {
        LoadedHome();
        _navigation.GoTo(new HomePageViewModel());

        _navigation.OnSignInChanged();
        Assert.Single(_catalog.HomeCalls);

        _navigation.GoHome();
        Assert.Equal(2, _catalog.HomeCalls.Count);
    }

    [Fact]
    public async Task AChipFilter_IsDropped_WithTheOldAccountsChips()
    {
        var home = LoadedHome();
        var chip = new HomeChip("Relax", "relax-params", IsSelected: false);
        var filtered = home.SelectChipCommand.ExecuteAsync(chip);
        _catalog.HomeCalls[1].Complete(null, "Relaxing");
        await filtered;

        _navigation.OnSignInChanged();

        Assert.Null(_catalog.HomeCalls[2].Chip);
    }

    [Fact]
    public void TheLibraryOnScreen_LoadsAgainInPlace_NotHome()
    {
        _navigation.GoLibrary();
        var old = _navigation.Current;
        var calls = _catalog.LibraryCalls.Count;

        _navigation.InvalidateLibrary();

        var library = Assert.IsType<LibraryPageViewModel>(_navigation.Current);
        Assert.NotSame(old, library);
        Assert.Equal(NavSection.Library, _navigation.Section);
        Assert.False(_navigation.CanGoBack);
        Assert.True(_catalog.LibraryCalls.Count > calls);
    }

    [Fact]
    public async Task AtStartup_HomeShowsLoading_AndAsksOnlyOnceTheSessionIsIn()
    {
        var session = new TaskCompletionSource();
        _navigation.SessionLoaded = session.Task;

        _navigation.GoHome();

        var home = Assert.IsType<HomePageViewModel>(_navigation.Current);
        Assert.True(home.IsLoading);
        Assert.Empty(_catalog.HomeCalls);

        session.SetResult();
        await Eventually(() => _catalog.HomeCalls.Count == 1);
        _catalog.HomeCalls[0].Complete(null, "Your mixes");
        Assert.Equal("Your mixes", Assert.Single(home.Shelves).Title);
        Assert.False(home.IsLoading);
    }

    [Fact]
    public async Task AtStartup_ASessionThatNeverLoads_DelaysHomeOnlyUpToTheCap()
    {
        _navigation.SessionLoaded = new TaskCompletionSource().Task;
        _navigation.SessionWait = TimeSpan.FromMilliseconds(50);

        _navigation.GoHome();

        await Eventually(() => _catalog.HomeCalls.Count == 1);
    }

    [Fact]
    public async Task AtStartup_TheLibraryAsksOnlyOnceTheSessionIsIn()
    {
        var session = new TaskCompletionSource();
        _navigation.SessionLoaded = session.Task;

        _navigation.GoLibrary();

        var library = Assert.IsType<LibraryPageViewModel>(_navigation.Current);
        Assert.True(library.SelectedSection.IsLoading);
        Assert.Empty(_catalog.LibraryCalls);

        session.SetResult();
        await Eventually(() => _catalog.LibraryCalls.Count == 1);
    }

    [Fact]
    public void OnceTheSessionIsIn_HomeAsksAtOnce()
    {
        _navigation.SessionLoaded = Task.CompletedTask;

        _navigation.GoHome();

        Assert.Single(_catalog.HomeCalls);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public void BeforeHomeWasEverOpened_NothingLoads()
    {
        _navigation.OnSignInChanged();

        Assert.Empty(_catalog.HomeCalls);
    }
}
