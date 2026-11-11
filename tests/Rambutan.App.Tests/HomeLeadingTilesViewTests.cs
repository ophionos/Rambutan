using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Models;
using HomePage = Rambutan.App.Views.HomePage;

namespace Rambutan.App.Tests;

/// <summary>
/// Home's leading tiles on screen: named buttons with the card's menu, one
/// tab stop with arrows inside, only whole rows that fit, and YouTube Music's
/// shelf not shown again below them.
/// </summary>
public class HomeLeadingTilesViewTests
{
    private sealed record Page(Window Window, HomePage View, ItemsControl Tiles);

    private static Page Open(double width, int tiles = 6)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var catalog = new FakeCatalog();
        var vm = new HomePageViewModel(catalog, null!) { IsSignedIn = () => true };
        var load = vm.EnsureLoadedAsync();
        catalog.HomeCalls[0].CompletePage(new Rambutan.Core.Models.HomePage
        {
            Shelves = [HomeLeadingBlockTests.Revisit(count: tiles), new Shelf("New releases", [FakeCatalog.Entry("Fresh")])],
        });
        load.GetAwaiter().GetResult();
        var view = new HomePage { DataContext = vm };
        var window = new Window { Width = width, Height = 700, DataContext = new MainViewModel(), Content = view };
        window.Show();
        Settle(window);
        var grid = view.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "TileGrid");
        return new Page(window, view, grid);
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static List<Button> Shown(Page page) =>
        [.. page.Tiles.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tile") && b.IsEffectivelyVisible)];

    [Fact]
    public void Each_tile_is_a_named_button_with_the_cards_menu() => Headless.Run(() =>
    {
        var page = Open(1000);

        var tiles = Shown(page);
        Assert.Equal(["Quick picks 1", "Quick picks 2", "Quick picks 3", "Quick picks 4", "Quick picks 5", "Quick picks 6"],
            tiles.Select(t => AutomationProperties.GetName(t)));
        Assert.Equal("Quick picks", AutomationProperties.GetName(page.Tiles));
        Assert.All(tiles, t =>
        {
            var host = t.FindAncestorOfType<Panel>()!;
            Assert.Same(t.DataContext, CollectionMenu.GetEntry(host));
            Assert.NotNull(host.ContextFlyout);
        });
        page.Window.Close();
    });

    [Fact]
    public void The_shelf_the_tiles_came_from_is_not_shown_again() => Headless.Run(() =>
    {
        var page = Open(1000);

        var shelves = page.View.GetVisualDescendants().OfType<ShelfView>()
            .Select(s => (s.DataContext as Shelf)?.Title).ToList();
        Assert.Equal(["New releases"], shelves);
        page.Window.Close();
    });

    [Fact]
    public void A_narrow_window_shows_two_whole_rows_and_no_hidden_tile_takes_focus() => Headless.Run(() =>
    {
        var page = Open(560, tiles: 12);

        var shown = Shown(page);
        Assert.InRange(shown.Count, 2, 11);
        Assert.Equal(0, shown.Count % 2);
        Assert.Equal(2, shown.Select(b => b.TranslatePoint(default, page.Tiles)!.Value.Y).Distinct().Count());
        Assert.Equal(shown.Count, RovingFocus.Stops(page.Tiles).Count);
        page.Window.Close();
    });

    [Fact]
    public void Arrows_move_between_the_tiles() => Headless.Run(() =>
    {
        var page = Open(1000);
        var tiles = Shown(page);
        tiles[0].Focus(NavigationMethod.Tab);

        page.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Settle(page.Window);

        Assert.Same(tiles[1], page.Window.FocusManager!.GetFocusedElement());
        page.Window.Close();
    });
}
