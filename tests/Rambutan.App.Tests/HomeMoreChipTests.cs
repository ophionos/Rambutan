using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>Home's chip row ends in a chip that opens Moods and genres, with or
/// without mood chips, and the header no longer has a button for it.</summary>
public class HomeMoreChipTests
{
    private static readonly HomeChip Relax = new("Relax", "RELAX", IsSelected: false);

    private sealed record Page(Window Window, NavigationService Navigation, HomePageViewModel Home, ItemsControl Row);

    private static Page Open(params HomeChip[] chips)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var catalog = new FakeCatalog { Moods = () => Task.FromResult(MoodsPage.Empty) };
        var navigation = new NavigationService(catalog, null!, null!);
        var vm = new HomePageViewModel(catalog, navigation);
        var load = vm.EnsureLoadedAsync();
        catalog.HomeCalls[0].CompleteWithChips(chips, null, "All hits");
        load.GetAwaiter().GetResult();
        var view = new Rambutan.App.Views.HomePage { DataContext = vm };
        var window = new Window { Width = 1000, Height = 700, DataContext = new MainViewModel(), Content = view };
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var row = view.GetVisualDescendants().OfType<ItemsControl>()
            .Single(i => AutomationProperties.GetName(i) == "Moods");
        return new Page(window, navigation, vm, row);
    }

    private static List<Button> Buttons(Page page) =>
        [.. page.Row.GetVisualDescendants().OfType<Button>()];

    [Fact]
    public void TheRow_EndsInAMoreChip_ThatOpensMoodsAndGenres() => Headless.Run(() =>
    {
        var page = Open(Relax);

        var buttons = Buttons(page);
        Assert.Equal(["Relax", "More"], buttons.Select(b => (string)b.Content!));
        var more = buttons[^1];
        Assert.Equal("Moods & genres", AutomationProperties.GetName(more));
        Assert.True(more.IsEffectivelyVisible);

        more.Command!.Execute(more.CommandParameter);

        Assert.IsType<MoodsPageViewModel>(page.Navigation.Current);
        page.Window.Close();
    });

    [Fact]
    public void WithoutMoodChips_TheMoreChipIsStillThere() => Headless.Run(() =>
    {
        var page = Open();

        var more = Assert.Single(Buttons(page));
        Assert.Equal("More", more.Content);
        Assert.True(more.IsEffectivelyVisible);
        more.Command!.Execute(more.CommandParameter);
        Assert.IsType<MoodsPageViewModel>(page.Navigation.Current);
        page.Window.Close();
    });

    [Fact]
    public void TheMoreChip_IsNotAMood_AndTheHeaderHasNoMoodsButton() => Headless.Run(() =>
    {
        var page = Open(Relax);

        Assert.Equal([Relax], page.Home.Chips);
        var named = page.Window.GetVisualDescendants().OfType<Button>()
            .Where(b => AutomationProperties.GetName(b) == "Moods & genres" || b.Content as string == "Moods & genres");
        Assert.Single(named);
        page.Window.Close();
    });
}
