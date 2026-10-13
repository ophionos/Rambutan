using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>
/// Moods &amp; genres: Home's mood chips, the grid of tiles, and a mood's
/// page with its "See all" links.
/// </summary>
public class MoodsTests
{
    private static readonly HomeChip Relax = new("Relax", "RELAX", IsSelected: false);
    private static readonly HomeChip Sleep = new("Sleep", "SLEEP", IsSelected: false);

    private static (HomePageViewModel Home, FakeCatalog Catalog) Home()
    {
        var catalog = new FakeCatalog();
        return (new HomePageViewModel(catalog, null!), catalog);
    }

    [Fact]
    public async Task AChip_FiltersHome_AndTheSelectedOne_TakesItBack()
    {
        var (home, catalog) = Home();
        var load = home.RefreshCommand.ExecuteAsync(null);
        catalog.HomeCalls[0].CompleteWithChips([Relax, Sleep], null, "All hits");
        await load;
        Assert.Equal([Relax, Sleep], home.Chips);

        var filter = home.SelectChipCommand.ExecuteAsync(Relax);
        Assert.Same(Relax, catalog.HomeCalls[1].Chip);
        // The shelves on show stay until the filtered ones come.
        Assert.Equal("All hits", Assert.Single(home.Shelves).Title);
        catalog.HomeCalls[1].CompleteWithChips([Relax with { IsSelected = true }, Sleep], null, "Yoga");
        await filter;
        Assert.Equal("Yoga", Assert.Single(home.Shelves).Title);

        var back = home.SelectChipCommand.ExecuteAsync(home.Chips[0]);
        Assert.Null(catalog.HomeCalls[2].Chip);
        catalog.HomeCalls[2].CompleteWithChips([Relax, Sleep], null, "All hits");
        await back;
        Assert.DoesNotContain(home.Chips, c => c.IsSelected);
    }

    [Fact]
    public async Task Refresh_KeepsTheChip()
    {
        var (home, catalog) = Home();
        var filter = home.SelectChipCommand.ExecuteAsync(Relax);
        catalog.HomeCalls[0].Complete(null, "Yoga");
        await filter;

        var refresh = home.RefreshCommand.ExecuteAsync(null);
        Assert.Same(Relax, catalog.HomeCalls[1].Chip);
        catalog.HomeCalls[1].Complete(null, "Yoga");
        await refresh;
    }

    [Fact]
    public async Task ASlowerEarlierChip_DoesNotOverwriteTheLaterOne()
    {
        var (home, catalog) = Home();
        var relax = home.SelectChipCommand.ExecuteAsync(Relax);
        var sleep = home.SelectChipCommand.ExecuteAsync(Sleep);

        catalog.HomeCalls[1].Complete(null, "Peaceful bedtime");
        await sleep;
        catalog.HomeCalls[0].Fail("late");
        catalog.HomeCalls[0].Complete(null, "Unwind");
        await relax;

        Assert.Equal("Peaceful bedtime", Assert.Single(home.Shelves).Title);
        Assert.Null(home.LoadError);
        Assert.False(home.IsLoading);
    }

    [Fact]
    public async Task AFailedChip_KeepsTheShelvesAndChips_AndSaysSo()
    {
        var (home, catalog) = Home();
        var load = home.RefreshCommand.ExecuteAsync(null);
        catalog.HomeCalls[0].CompleteWithChips([Relax], null, "All hits");
        await load;

        var filter = home.SelectChipCommand.ExecuteAsync(Relax);
        catalog.HomeCalls[1].Fail("offline");
        await filter;

        Assert.NotNull(home.LoadError);
        Assert.Equal("All hits", Assert.Single(home.Shelves).Title);
        Assert.Equal([Relax], home.Chips);
    }

    [Fact]
    public async Task Grid_ShowsItsGroups_OrSaysItIsEmpty_OrFails()
    {
        var catalog = new FakeCatalog
        {
            Moods = () => Task.FromResult(new MoodsPage
            {
                Sections = [new MoodSection("Genres", [new MoodCategory("Jazz", "JAZZ")])],
            }),
        };
        var page = new MoodsPageViewModel(catalog, null!);
        Assert.True(page.IsLoading);
        await page.LoadAsync();
        Assert.Equal("Genres", Assert.Single(page.Sections).Title);
        Assert.False(page.IsEmpty);
        Assert.False(page.IsLoading);

        catalog.Moods = () => Task.FromResult(MoodsPage.Empty);
        await page.LoadAsync();
        Assert.True(page.IsEmpty);

        catalog.Moods = () => Task.FromException<MoodsPage>(new HttpRequestException("offline"));
        await page.LoadAsync();
        Assert.NotNull(page.LoadError);
        Assert.False(page.IsEmpty);
    }

    [Fact]
    public async Task CategoryPage_TakesItsOwnTitle_AndSeeAllOnlyWhereThereIsMore()
    {
        var more = new MoodCategory("Coffee shop blends", "COFFEE");
        var catalog = new FakeCatalog
        {
            MoodPage = c => Task.FromResult(new MoodCategoryPage
            {
                Title = "Chill",
                Shelves =
                [
                    new Shelf("Coffee shop blends", [FakeCatalog.Entry("a")]) { More = more },
                    new Shelf("Featured playlists", [FakeCatalog.Entry("b")]),
                ],
            }),
        };
        var page = new MoodCategoryPageViewModel(catalog, null!, new MoodCategory("chill tile", "CHILL"));
        Assert.Equal("chill tile", page.Title);

        await page.LoadAsync();

        Assert.Equal("Chill", page.Title);
        Assert.NotNull(page.Shelves[0].SeeAll);
        Assert.Null(page.Shelves[1].SeeAll);
        Assert.False(page.IsEmpty);
    }

    [Fact]
    public async Task SeeAll_OpensThePageItLinks()
    {
        var more = new MoodCategory("Coffee shop blends", "COFFEE");
        var asked = new List<string>();
        var catalog = new FakeCatalog
        {
            MoodPage = c =>
            {
                asked.Add(c.Params);
                return Task.FromResult(new MoodCategoryPage
                {
                    Shelves = [new Shelf("Coffee shop blends", [FakeCatalog.Entry("a")]) { More = more }],
                });
            },
        };
        var navigation = new NavigationService(catalog, null!, null!);
        navigation.OpenMoodCategory(new MoodCategory("Chill", "CHILL"));
        var page = Assert.IsType<MoodCategoryPageViewModel>(navigation.Current);
        await page.LoadAsync();

        page.Shelves[0].SeeAll!.Execute(page.Shelves[0].Shelf);

        var opened = Assert.IsType<MoodCategoryPageViewModel>(navigation.Current);
        Assert.Equal("Coffee shop blends", opened.Title);
        Assert.Contains("COFFEE", asked);
        Assert.True(navigation.CanGoBack);
    }
}
