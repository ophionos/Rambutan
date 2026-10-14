using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>The mini player: the window shrinks to the player bar, keeps bounds
/// of its own, and leaving puts the full window back as it was.</summary>
public class MiniPlayerTests
{
    private static MainWindow Show(MainViewModel vm, AppSettings? settings = null)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var window = new MainWindow { DataContext = vm };
        if (settings is not null)
            window.AttachPersistence(WindowStatePersistence.Attach(window, settings));
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Button Named(Window window, string name) =>
        window.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == name && b.IsEffectivelyVisible);

    private static bool Shows(Window window, string name) =>
        window.GetVisualDescendants().OfType<Button>()
            .Any(b => AutomationProperties.GetName(b) == name && b.IsEffectivelyVisible);

    private static Border Part(Window window, string name) =>
        window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == name);

    private sealed class Page : ViewModelBase;

    [Fact]
    public void TheMiniPlayer_IsTheBarAlone_WithItsOwnButtons() => Headless.Run(() =>
    {
        var vm = new MainViewModel();
        var window = Show(vm);
        Assert.True(Shows(window, "Shuffle: off"));
        Assert.False(Shows(window, "Keep on top"));

        vm.IsMiniPlayer = true;
        Settle(window);

        Assert.False(Part(window, "TitleBar").IsEffectivelyVisible);
        Assert.False(Part(window, "Sidebar").IsEffectivelyVisible);
        Assert.True(Part(window, "PlayerBar").IsEffectivelyVisible);
        Assert.Equal(WindowDecorations.BorderOnly, window.WindowDecorations);
        foreach (var gone in new[] { "Shuffle: off", "Repeat: off", "Mute" })
            Assert.False(Shows(window, gone), gone);
        foreach (var shown in new[] { "Previous", "Play", "Next", "Volume", "Keep on top", "Full window", "Close" })
            Assert.True(Shows(window, shown), shown);
        // The shortcut is the button's accelerator, not part of its name.
        Assert.Equal("Ctrl+M", AutomationProperties.GetAcceleratorKey(Named(window, "Full window")));

        vm.IsMiniPlayer = false;
        Settle(window);

        Assert.True(Part(window, "TitleBar").IsEffectivelyVisible);
        Assert.True(Part(window, "Sidebar").IsEffectivelyVisible);
        Assert.Equal(WindowDecorations.Full, window.WindowDecorations);
        Assert.True(Shows(window, "Shuffle: off"));
        Assert.True(Shows(window, "Mute"));
        Assert.False(Shows(window, "Keep on top"));
        window.Close();
    });

    [Fact]
    public void ThePin_KeepsOnlyTheMiniPlayerOnTop() => Headless.Run(() =>
    {
        var vm = new MainViewModel { IsPinned = true };
        var window = Show(vm);
        Assert.False(window.Topmost);

        vm.IsMiniPlayer = true;
        Settle(window);
        Assert.True(window.Topmost);
        Assert.True(Shows(window, "Stop keeping on top"));

        Named(window, "Stop keeping on top").Command!.Execute(null);
        Assert.False(vm.IsPinned);
        Assert.False(window.Topmost);

        vm.IsPinned = true;
        vm.IsMiniPlayer = false;
        Settle(window);
        Assert.False(window.Topmost);
        window.Close();
    });

    [Fact]
    public void WhereNoWindowCanStayOnTop_ThePinSaysSo_AndDoesNothing() => Headless.Run(() =>
    {
        var vm = new MainViewModel { IsPinned = true, IsMiniPlayer = true };
        var window = Show(vm);
        vm.CanPin = false;
        Settle(window);

        Assert.False(window.Topmost);
        var pin = window.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b)?.Contains("Wayland") == true);
        Assert.True(pin.IsEffectivelyVisible);
        Assert.False(pin.IsEffectivelyEnabled);
        Assert.True(ToolTip.GetShowOnDisabled(pin));
        window.Close();
    });

    [Fact]
    public void TheMiniPlayer_KeepsItsOwnBounds_AndLeavingPutsTheFullWindowBack() => Headless.Run(() =>
    {
        var settings = new AppSettings { WindowWidth = 1100, WindowHeight = 700 };
        var vm = new MainViewModel();
        var window = Show(vm, settings);
        Assert.Equal(1100, window.Width);

        vm.IsMiniPlayer = true;
        Settle(window);

        // The first time, from its minimum, with the title at the full bar's floor.
        Assert.Equal(window.MiniMinWidth + 80, window.Width);
        Assert.Equal(88, window.Height);
        window.Width = 900;
        Settle(window);
        Assert.Equal(900, settings.MiniWindowWidth);
        Assert.Equal(1100, settings.WindowWidth);
        Assert.Equal(700, settings.WindowHeight);

        vm.IsMiniPlayer = false;
        Settle(window);
        Assert.Equal(1100, window.Width);
        Assert.Equal(700, window.Height);

        vm.IsMiniPlayer = true;
        Settle(window);
        Assert.Equal(900, window.Width);
        window.Close();
    });

    [Fact]
    public void AMaximizedWindow_ComesBackMaximized() => Headless.Run(() =>
    {
        var settings = new AppSettings { WindowWidth = 1100, WindowHeight = 700, WindowMaximized = true };
        var vm = new MainViewModel();
        var window = Show(vm, settings);
        Assert.Equal(WindowState.Maximized, window.WindowState);

        vm.IsMiniPlayer = true;
        Settle(window);
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.True(settings.WindowMaximized);

        vm.IsMiniPlayer = false;
        Settle(window);
        Assert.Equal(WindowState.Maximized, window.WindowState);
        window.Close();
    });

    [Fact]
    public void AStartInTheMiniPlayer_OpensAsTheMiniPlayer() => Headless.Run(() =>
    {
        var settings = new AppSettings { WindowWidth = 1100, WindowHeight = 700, MiniWindowWidth = 720 };
        var vm = new MainViewModel { IsMiniPlayer = true };
        var window = Show(vm, settings);

        Assert.Equal(720, window.Width);
        Assert.Equal(88, window.Height);
        Assert.False(Part(window, "TitleBar").IsEffectivelyVisible);

        vm.IsMiniPlayer = false;
        Settle(window);
        Assert.Equal(1100, window.Width);
        window.Close();
    });

    [Fact]
    public void TheMiniPlayer_GrowsForTheStatusStrip_AndShrinksBack() => Headless.Run(() =>
    {
        var vm = new MainViewModel { IsMiniPlayer = true };
        var window = Show(vm);
        Assert.Equal(88, window.Height);

        vm.TrackWaitingForNetwork = new TrackInfo { VideoId = "a", Title = "Aerodynamic", Artists = [] };
        Settle(window);
        Assert.True(window.Height > 88);
        Assert.True(Part(window, "StatusStrip").Bounds.Height > 0);

        vm.TrackWaitingForNetwork = null;
        Settle(window);
        Assert.Equal(88, window.Height);
        window.Close();
    });

    [Fact]
    public void CtrlM_SwitchesBothWays() => Headless.Run(() =>
    {
        var vm = new MainViewModel();
        var window = Show(vm);

        window.KeyPress(Key.M, RawInputModifiers.Control, PhysicalKey.M, "m");
        Assert.True(vm.IsMiniPlayer);
        window.KeyPress(Key.M, RawInputModifiers.Control, PhysicalKey.M, "m");
        Assert.False(vm.IsMiniPlayer);
        window.Close();
    });

    [Fact]
    public void APageOpenedFromTheMiniPlayer_BringsTheFullWindowBack() => Headless.Run(() =>
    {
        var vm = new MainViewModel { IsMiniPlayer = true };

        // The app's first page, at a start in the mini player, stays behind it.
        vm.Navigation.GoTo(new Page());
        Assert.True(vm.IsMiniPlayer);

        vm.Navigation.GoTo(new Page());
        Assert.False(vm.IsMiniPlayer);
    });

    [Fact]
    public void TheArtwork_InTheMiniPlayer_OpensNowPlayingInTheFullWindow() => Headless.Run(() =>
    {
        var vm = new MainViewModel { IsMiniPlayer = true };

        vm.ToggleNowPlayingCommand.Execute(null);

        Assert.False(vm.IsMiniPlayer);
        Assert.True(vm.NowPlaying.IsOpen);

        vm.ToggleNowPlayingCommand.Execute(null);
        Assert.False(vm.NowPlaying.IsOpen);
    });
}
