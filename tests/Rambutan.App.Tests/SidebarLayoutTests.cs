using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;

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
