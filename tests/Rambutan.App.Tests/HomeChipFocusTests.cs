using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>
/// Home's mood chips from the keyboard: one tab stop for the row, arrows
/// inside it, and focus kept on the pressed chip when the filtered page replaces
/// the chips (YTM resends them, sometimes in another order).
/// </summary>
public class HomeChipFocusTests
{
    private static readonly HomeChip Relax = new("Relax", "RELAX", IsSelected: false);
    private static readonly HomeChip Sleep = new("Sleep", "SLEEP", IsSelected: false);
    private static readonly HomeChip Sad = new("Sad", "SAD", IsSelected: false);

    private sealed record Page(Window Window, ItemsControl Chips, FakeCatalog Catalog);

    private static Page Open()
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var catalog = new FakeCatalog();
        var vm = new HomePageViewModel(catalog, null!);
        var load = vm.EnsureLoadedAsync();
        catalog.HomeCalls[0].CompleteWithChips([Relax, Sleep, Sad], null, "All hits");
        load.GetAwaiter().GetResult();
        var view = new Rambutan.App.Views.HomePage { DataContext = vm };
        var window = new Window { Width = 1000, Height = 700, DataContext = new MainViewModel(), Content = view };
        window.Show();
        Settle(window);
        var chips = view.GetVisualDescendants().OfType<ItemsControl>()
            .Single(i => AutomationProperties.GetName(i) == "Moods");
        return new Page(window, chips, catalog);
    }

    private static Button Chip(Page page, string title) =>
        page.Chips.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == title);

    private static string? FocusedName(Window window) =>
        window.FocusManager!.GetFocusedElement() is Control c ? AutomationProperties.GetName(c) : null;

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void Arrows_MoveAlongTheChips() => Headless.Run(() =>
    {
        var page = Open();
        Chip(page, "Relax").Focus(NavigationMethod.Tab);

        page.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Settle(page.Window);

        Assert.Equal("Sleep", FocusedName(page.Window));
        page.Window.Close();
    });

    [Fact]
    public void Enter_OnAChip_KeepsFocusOnIt_WhenTheChipsComeBack() => Headless.Run(() =>
    {
        var page = Open();
        Chip(page, "Sad").Focus(NavigationMethod.Tab);

        page.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(page.Window);
        Assert.Same(Sad, page.Catalog.HomeCalls[1].Chip);
        // The filtered page sends the chips again, in another order.
        page.Catalog.HomeCalls[1].CompleteWithChips([Relax, Sad with { IsSelected = true }, Sleep], null, "Sad songs");
        Settle(page.Window);

        Assert.Equal("Sad, selected", FocusedName(page.Window));
        page.Window.Close();
    });
}
