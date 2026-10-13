using Rambutan.App.ViewModels;

namespace Rambutan.App.Tests;

/// <summary>
/// Offline, Home's error points to what still works: downloads play, and the
/// Library opens the downloaded albums and playlists.
/// </summary>
public class OfflineHomeTests
{
    private static (HomePageViewModel Home, FakeCatalog Catalog, NavigationService Nav) Home()
    {
        var catalog = new FakeCatalog();
        var nav = new NavigationService(catalog, null!, null!);
        return (new HomePageViewModel(catalog, nav), catalog, nav);
    }

    [Fact]
    public async Task Offline_OffersDownloads_AndMentionsTheLibrary()
    {
        var (home, catalog, nav) = Home();
        var opened = 0;
        nav.GoDownloads = () => opened++;

        var load = home.RefreshCommand.ExecuteAsync(null);
        catalog.HomeCalls[0].Fail("No such host is known.");
        await load;

        var error = Assert.IsType<LoadError>(home.LoadError);
        Assert.True(error.IsOffline);
        Assert.Contains("Library", error.Hint);
        Assert.NotNull(error.Retry);
        error.GoToDownloads!.Execute(null);
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task WithoutDownloads_TheErrorStaysAsItWas()
    {
        var (home, catalog, _) = Home();

        var load = home.RefreshCommand.ExecuteAsync(null);
        catalog.HomeCalls[0].Fail("No such host is known.");
        await load;

        Assert.Null(home.LoadError!.GoToDownloads);
        Assert.Equal("Check your connection, then try again.", home.LoadError.Hint);
    }

    [Fact]
    public void AnAnsweredRequest_IsNotOffline() =>
        Assert.False(LoadError.From(
            new HttpRequestException("x", null, System.Net.HttpStatusCode.BadGateway),
            LoadSubject.Home, null).IsOffline);
}
