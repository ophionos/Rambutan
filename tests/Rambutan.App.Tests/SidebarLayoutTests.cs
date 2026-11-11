using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Sidebar;

namespace Rambutan.App.Tests;

/// <summary>The sidebar's nav list never runs under the account chip: the window's
/// minimum height fits both, and a window kept shorter anyway scrolls the list.</summary>
public class SidebarLayoutTests
{
    [Fact]
    public void TheMinimum_FitsTheWholeNavListAboveTheAccountChip() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var window = new MainWindow { DataContext = new MainViewModel(), Width = 1280, Height = 800 };
        window.Show();
        Settle(window);

        Assert.InRange(window.SidebarMinHeight, 300, 700);
        Assert.Equal(window.SidebarMinHeight, window.MinHeight);

        window.Height = window.SidebarMinHeight;
        Settle(window);

        var scroll = Named<ScrollViewer>(window, "SidebarNavScroll");
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 0.5,
            $"the list scrolls at the minimum: {scroll.Extent.Height} in {scroll.Viewport.Height}");
        Assert.True(Bottom(window, scroll) <= Top(window, Named<StackPanel>(window, "SidebarAccount")) + 0.5);
        window.Close();
    });

    [Fact]
    public void AWindowKeptShorterAnyway_ScrollsTheList_AndNeverDrawsTheChipOverIt() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var window = new MainWindow { DataContext = new MainViewModel(), Width = 1280, Height = 300 };
        window.Show();
        Settle(window);

        // A tiling window manager made it shorter than the minimum: the layout follows the window.
        Assert.Equal(0, window.MinHeight);
        Assert.Equal(300, window.Bounds.Height, 0.5);

        var scroll = Named<ScrollViewer>(window, "SidebarNavScroll");
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        Assert.True(Bottom(window, scroll) <= Top(window, Named<StackPanel>(window, "SidebarAccount")) + 0.5);
        window.Close();
    });

    [Fact]
    public void TheSignInButton_RaisesTheMinimum() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Settle(window);
        var signIn = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("signin"));
        var before = window.SidebarMinHeight;

        signIn.IsVisible = !signIn.IsVisible;
        Settle(window);

        Assert.NotEqual(before, window.SidebarMinHeight);
        Assert.Equal(signIn.IsVisible, window.SidebarMinHeight > before);
        window.Close();
    });

    private static (MainWindow Window, MainViewModel Vm, SidebarPinLibrary Pins) WithPins(int count)
    {
        ShellResources.Ensure();
        var pins = new SidebarPinLibrary(new MemorySidebarPinStore());
        for (var i = 0; i < count; i++)
            pins.Pin(new SidebarPin(PinKind.Playlist, "PL" + i, "Playlist " + i));
        var vm = new MainViewModel { SidebarPins = new SidebarPinsViewModel(pins, null, _ => { }, action => action()) };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Settle(window);
        return (window, vm, pins);
    }

    private static List<Button> PinRows(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("pin")).ToList();

    [Fact]
    public void ThePins_NeverRaiseTheMinimum_AndHideInTheRail() => Headless.Run(() =>
    {
        var (window, vm, pins) = WithPins(0);
        var nav = Named<StackPanel>(window, "SidebarNav");
        var section = Named<Panel>(window, "SidebarPinsSection");
        Assert.True(section.IsEffectivelyVisible);
        var navHeight = nav.DesiredSize.Height;
        var minimum = window.SidebarMinHeight;

        for (var i = 0; i < 12; i++)
            pins.Pin(new SidebarPin(PinKind.Playlist, "PL" + i, "Playlist " + i));
        Settle(window);

        // The minimum counts the destinations only; the pins scroll.
        Assert.Equal(navHeight, nav.DesiredSize.Height);
        Assert.Equal(minimum, window.SidebarMinHeight);
        var rows = PinRows(window);
        Assert.Equal(12, rows.Count);
        Assert.Equal("Playlist 0", AutomationProperties.GetName(rows[0]));
        Assert.All(rows, r => Assert.True(r.IsTabStop && r.Focusable));

        vm.IsSidebarOpen = false;
        Settle(window);
        Assert.False(section.IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public void APinRow_OpensItsMenuFromTheKeyboard_AndAltArrowsMoveIt_KeepingTheFocus() => Headless.Run(() =>
    {
        var (window, _, pins) = WithPins(3);
        var first = PinRows(window)[0];
        first.Focus();

        first.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = first });
        var flyout = Assert.IsType<MenuFlyout>(first.ContextFlyout);
        Assert.True(flyout.IsOpen);
        Assert.Equal("Open", ((MenuItem)flyout.Items[0]!).Header);
        flyout.Hide();

        window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
        Settle(window);

        Assert.Equal(["PL1", "PL0", "PL2"], pins.Pins.Select(p => p.Id));
        var focused = Assert.IsAssignableFrom<Button>(window.FocusManager?.GetFocusedElement());
        Assert.Equal("Playlist 0", AutomationProperties.GetName(focused));
        Assert.Same(PinRows(window)[1], focused);
        window.Close();
    });

    private static double Top(Window window, Control c) => c.TranslatePoint(default, window)!.Value.Y;

    private static double Bottom(Window window, Control c) =>
        c.TranslatePoint(new Point(0, c.Bounds.Height), window)!.Value.Y;

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++) // the minimum is set after a layout pass
        {
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }
}
