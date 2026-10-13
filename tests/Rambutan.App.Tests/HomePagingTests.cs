using Rambutan.App.ViewModels;

namespace Rambutan.App.Tests;

/// <summary>
/// Home loads more shelves as the list nears its end, the way YTM's own client
/// does; before, only the first page (two or three shelves) ever showed.
/// </summary>
public class HomePagingTests
{
    private static (HomePageViewModel Home, FakeCatalog Catalog) Home()
    {
        var catalog = new FakeCatalog();
        return (new HomePageViewModel(catalog, null!), catalog);
    }

    private static string[] Titles(HomePageViewModel home) => home.Shelves.Select(s => s.Title).ToArray();

    [Fact]
    public async Task MorePages_AppendShelves_UntilNoTokenIsLeft()
    {
        var (home, catalog) = Home();
        var load = home.RefreshCommand.ExecuteAsync(null);
        catalog.HomeCalls[0].Complete("p2", "Listen again", "New releases");
        await load;
        Assert.True(home.HasMore);

        var more = home.LoadMoreAsync();
        Assert.Equal("p2", catalog.HomeCalls[1].Token?.Value);
        Assert.True(home.IsLoadingMore);
        catalog.HomeCalls[1].Complete(null, "Quick picks");
        await more;

        Assert.Equal(["Listen again", "New releases", "Quick picks"], Titles(home));
        Assert.False(home.HasMore);
        Assert.False(home.IsLoadingMore);

        await home.LoadMoreAsync();
        Assert.Equal(2, catalog.HomeCalls.Count);
    }

    [Fact]
    public async Task OnlyOnePageIsInFlight()
    {
        var (home, catalog) = Home();
        var load = home.RefreshCommand.ExecuteAsync(null);
        catalog.HomeCalls[0].Complete("p2", "Listen again");
        await load;

        var first = home.LoadMoreAsync();
        var second = home.LoadMoreAsync();
        await second;

        Assert.Equal(2, catalog.HomeCalls.Count);
        catalog.HomeCalls[1].Complete(null, "Quick picks");
        await first;
    }

    [Fact]
    public async Task AFailedPage_KeepsTheShelves_AndRetriesTheSameToken()
    {
        var (home, catalog) = Home();
        var load = home.RefreshCommand.ExecuteAsync(null);
        catalog.HomeCalls[0].Complete("p2", "Listen again");
        await load;

        var more = home.LoadMoreAsync();
        catalog.HomeCalls[1].Fail("offline");
        await more;

        Assert.Equal(["Listen again"], Titles(home));
        Assert.NotNull(home.MoreError);
        Assert.Null(home.LoadError);
        Assert.True(home.HasMore);
        Assert.False(home.IsLoadingMore);

        var retry = home.MoreError!.Retry!;
        retry.Execute(null);
        Assert.Equal("p2", catalog.HomeCalls[2].Token?.Value);
        Assert.Null(home.MoreError);
        catalog.HomeCalls[2].Complete(null, "Quick picks");
    }

    [Fact]
    public async Task ARefresh_DropsAPageAskedForBeforeIt()
    {
        var (home, catalog) = Home();
        var load = home.RefreshCommand.ExecuteAsync(null);
        catalog.HomeCalls[0].Complete("p2", "Old first");
        await load;

        var staleMore = home.LoadMoreAsync();
        var refresh = home.RefreshCommand.ExecuteAsync(null);
        catalog.HomeCalls[2].Complete("fresh-p2", "New first");
        await refresh;
        catalog.HomeCalls[1].Complete(null, "Stale second");
        await staleMore;

        Assert.Equal(["New first"], Titles(home));
        Assert.True(home.HasMore);
        Assert.False(home.IsLoadingMore);

        var more = home.LoadMoreAsync();
        Assert.Equal("fresh-p2", catalog.HomeCalls[3].Token?.Value);
        catalog.HomeCalls[3].Complete(null, "New second");
        await more;
        Assert.Equal(["New first", "New second"], Titles(home));
    }

    [Fact]
    public async Task NoMoreIsAskedForWhileTheFirstPageLoads()
    {
        var (home, catalog) = Home();
        var load = home.RefreshCommand.ExecuteAsync(null);

        await home.LoadMoreAsync();

        Assert.Single(catalog.HomeCalls);
        catalog.HomeCalls[0].Complete(null, "Listen again");
        await load;
        Assert.False(home.HasMore);
    }
}
