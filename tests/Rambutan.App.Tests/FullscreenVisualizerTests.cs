using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Models;
using Rambutan.Core.Settings;

namespace Rambutan.App.Tests;

/// <summary>The fullscreen visualizer: what it shows, when its controls and
/// the title come and go, and how the window gets in and out of it.</summary>
public class FullscreenVisualizerTests
{
    private static readonly TrackInfo First = new() { VideoId = "a", Title = "A", Artists = [] };
    private static readonly TrackInfo Second = new() { VideoId = "b", Title = "B", Artists = [] };

    /// <summary>Every timer the view model starts, driven by hand.</summary>
    private sealed class FakeTimers
    {
        private readonly List<(TimeSpan Interval, Action Tick)> _running = [];

        public IDisposable Every(TimeSpan interval, Action action)
        {
            var entry = (interval, action);
            _running.Add(entry);
            return new Stop(() => _running.Remove(entry));
        }

        public bool Runs(TimeSpan interval) => _running.Any(t => t.Interval == interval);

        /// <summary>Ticks the fullscreen timer for <paramref name="time"/>.</summary>
        public void Pass(TimeSpan time)
        {
            for (var at = TimeSpan.Zero; at < time; at += NowPlayingViewModel.FullscreenTick)
            {
                foreach (var (_, tick) in _running.Where(t => t.Interval == NowPlayingViewModel.FullscreenTick).ToList())
                    tick();
            }
        }

        private sealed class Stop(Action stop) : IDisposable
        {
            public void Dispose() => stop();
        }
    }

    private static (NowPlayingViewModel Vm, AppearanceSettings Appearance, AppSettings Saved, FakeTimers Timers) NowPlaying(
        MotionPreference motion = MotionPreference.Full)
    {
        var appearance = new AppearanceSettings { Motion = motion };
        var player = new PlayerBarViewModel
        {
            NowPlaying = First, ShownTrack = First, ArtworkUrl = "https://example.com/a.jpg", IsPlaying = true,
        };
        var saved = new AppSettings();
        var timers = new FakeTimers();
        var vm = new NowPlayingViewModel(player, settings: saved, appearance: appearance, every: timers.Every);
        return (vm, appearance, saved, timers);
    }

    [Fact]
    public void Fullscreen_ShowsTheStage_WithoutChangingItsToggle()
    {
        var (vm, _, saved, _) = NowPlaying();
        Assert.False(vm.IsStageShown);

        vm.ToggleFullscreenCommand.Execute(null);

        Assert.True(vm.IsOpen);
        Assert.True(vm.IsStageShown);
        Assert.True(vm.IsStageActive);
        Assert.False(vm.IsIdentityShown);
        Assert.False(vm.IsWindowed);
        Assert.False(vm.ShowStage);
        Assert.False(saved.ShowVisualizerStage);

        vm.ToggleFullscreenCommand.Execute(null);
        Assert.False(vm.IsStageShown);
        Assert.True(vm.IsIdentityShown);
    }

    [Fact]
    public void TheControls_ShowOnEntry_AndHideAfterAWaitWithoutInput()
    {
        var (vm, _, _, timers) = NowPlaying();
        vm.IsFullscreen = true;
        Assert.True(vm.IsChromeShown);
        Assert.False(vm.IsChromeHidden);

        timers.Pass(NowPlayingViewModel.ChromeDelay - NowPlayingViewModel.FullscreenTick);
        Assert.True(vm.IsChromeShown);
        vm.ShowChrome();
        timers.Pass(NowPlayingViewModel.ChromeDelay - NowPlayingViewModel.FullscreenTick);
        Assert.True(vm.IsChromeShown);

        timers.Pass(NowPlayingViewModel.FullscreenTick);
        Assert.False(vm.IsChromeShown);
        Assert.True(vm.IsChromeHidden);
    }

    [Fact]
    public void HeldControls_Stay_AndWaitAgainOnceLetGo()
    {
        var (vm, _, _, timers) = NowPlaying();
        vm.IsFullscreen = true;
        vm.HoldChrome(true);
        timers.Pass(NowPlayingViewModel.ChromeDelay * 3);
        Assert.True(vm.IsChromeShown);

        vm.HoldChrome(false);
        timers.Pass(NowPlayingViewModel.ChromeDelay - NowPlayingViewModel.FullscreenTick);
        Assert.True(vm.IsChromeShown);
        timers.Pass(NowPlayingViewModel.FullscreenTick);
        Assert.False(vm.IsChromeShown);
    }

    [Fact]
    public void TheTitle_ShowsOnEntryAndOnEachNewTrack_ForAWhile()
    {
        var (vm, _, _, timers) = NowPlaying();
        vm.IsFullscreen = true;
        timers.Pass(NowPlayingViewModel.ChromeDelay);
        Assert.False(vm.IsChromeShown);
        Assert.True(vm.IsTitleCardShown);

        timers.Pass(NowPlayingViewModel.TitleCardTime);
        Assert.False(vm.IsTitleCardShown);
        // Nothing waits to hide, so nothing ticks.
        Assert.False(timers.Runs(NowPlayingViewModel.FullscreenTick));

        vm.Player.NowPlaying = Second;
        vm.Player.ShownTrack = Second;
        Assert.True(vm.IsTitleCardShown);
        Assert.False(vm.IsChromeShown);

        // With the controls, the title shows too.
        timers.Pass(NowPlayingViewModel.TitleCardTime);
        Assert.False(vm.IsTitleCardShown);
        vm.ShowChrome();
        Assert.True(vm.IsTitleCardShown);
    }

    [Fact]
    public void OutsideFullscreen_InputShowsNothing_AndNothingTicks()
    {
        var (vm, _, _, timers) = NowPlaying();
        vm.IsOpen = true;
        vm.ShowChrome();
        Assert.False(vm.IsChromeShown);
        Assert.False(vm.IsTitleCardShown);
        Assert.False(timers.Runs(NowPlayingViewModel.FullscreenTick));
    }

    [Fact]
    public void ClosingNowPlaying_LeavesFullscreen_AndStopsItsTimer()
    {
        var (vm, _, _, timers) = NowPlaying();
        vm.IsFullscreen = true;
        Assert.True(timers.Runs(NowPlayingViewModel.FullscreenTick));

        vm.CloseCommand.Execute(null);

        Assert.False(vm.IsFullscreen);
        Assert.False(vm.IsChromeShown);
        Assert.False(vm.IsTitleCardShown);
        Assert.False(timers.Runs(NowPlayingViewModel.FullscreenTick));
    }

    [Fact]
    public void ReducedMotion_KeepsTheArtworkAndTitle_InFullscreen()
    {
        var (vm, _, _, _) = NowPlaying(MotionPreference.Reduced);
        vm.IsFullscreen = true;

        Assert.False(vm.IsStageVisualizerShown);
        Assert.True(vm.IsIdentityShown);
        // The band says why nothing moves.
        Assert.True(vm.IsBandShown);
        Assert.False(vm.IsBandActive);
        // The artwork's own title shows: no second one over it.
        Assert.False(vm.IsTitleCardShown);
    }

    [Fact]
    public void Fullscreen_AndTheMiniPlayer_TakeTurns()
    {
        var vm = new MainViewModel { IsMiniPlayer = true };
        vm.NowPlaying.IsFullscreen = true;
        Assert.False(vm.IsMiniPlayer);
        Assert.True(vm.NowPlaying.IsOpen);

        vm.IsMiniPlayer = true;
        Assert.False(vm.NowPlaying.IsFullscreen);
    }

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

    private static Border Part(Window window, string name) =>
        window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == name);

    private static void Press(Window window, Key key)
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key };
        window.RaiseEvent(args);
    }

    [Fact]
    public void TheWindow_GivesItselfToTheVisualizer_AndTakesItBack() => Headless.Run(() =>
    {
        var vm = new MainViewModel();
        var window = Show(vm);
        window.WindowState = WindowState.Maximized;
        Settle(window);

        vm.NowPlaying.IsFullscreen = true;
        Settle(window);

        Assert.Equal(WindowState.FullScreen, window.WindowState);
        Assert.False(Part(window, "TitleBar").IsEffectivelyVisible);
        Assert.False(Part(window, "Sidebar").IsEffectivelyVisible);
        Assert.False(Part(window, "PlayerBar").IsEffectivelyVisible);
        Assert.True(Part(window, "ContentPanel").IsEffectivelyVisible);
        Assert.Equal(default, Part(window, "ContentPanel").Margin);

        Press(window, Key.Escape);
        Settle(window);

        Assert.False(vm.NowPlaying.IsFullscreen);
        // Esc left fullscreen only: Now Playing stays open.
        Assert.True(vm.NowPlaying.IsOpen);
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.True(Part(window, "TitleBar").IsEffectivelyVisible);
        Assert.True(Part(window, "PlayerBar").IsEffectivelyVisible);
        Assert.NotEqual(default, Part(window, "ContentPanel").Margin);
    });

    [Fact]
    public void F11AndEsc_LeaveTheWholeAppsFullscreen_ToTheStateBefore() => Headless.Run(() =>
    {
        var vm = new MainViewModel();
        var window = Show(vm);
        window.WindowState = WindowState.Maximized;
        Settle(window);

        Press(window, Key.F11);
        Settle(window);
        Assert.Equal(WindowState.FullScreen, window.WindowState);
        // The whole app: the frame stays.
        Assert.True(Part(window, "PlayerBar").IsEffectivelyVisible);

        Press(window, Key.Escape);
        Settle(window);
        Assert.Equal(WindowState.Maximized, window.WindowState);

        Press(window, Key.F11);
        Press(window, Key.F11);
        Settle(window);
        Assert.Equal(WindowState.Maximized, window.WindowState);
    });

    [Fact]
    public void TheVisualizerOverTheWholeAppsFullscreen_LeavesItFullscreen() => Headless.Run(() =>
    {
        var vm = new MainViewModel();
        var window = Show(vm);
        window.WindowState = WindowState.FullScreen;
        Settle(window);

        vm.NowPlaying.IsFullscreen = true;
        Settle(window);
        vm.NowPlaying.IsFullscreen = false;
        Settle(window);

        Assert.Equal(WindowState.FullScreen, window.WindowState);
        Assert.True(Part(window, "PlayerBar").IsEffectivelyVisible);
    });

    [Fact]
    public void LeavingFullscreenOutsideTheApp_EndsTheVisualizers() => Headless.Run(() =>
    {
        var vm = new MainViewModel();
        var window = Show(vm);
        vm.NowPlaying.IsFullscreen = true;
        Settle(window);

        window.WindowState = WindowState.Normal;
        Settle(window);

        Assert.False(vm.NowPlaying.IsFullscreen);
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.True(Part(window, "PlayerBar").IsEffectivelyVisible);
    });

    [Theory]
    [InlineData(WindowState.FullScreen, WindowState.Normal, true)]
    [InlineData(WindowState.FullScreen, WindowState.Maximized, true)]
    [InlineData(WindowState.FullScreen, WindowState.Minimized, false)]
    // Wayland, while the compositor hasn't confirmed fullscreen yet.
    [InlineData(WindowState.Normal, WindowState.Normal, false)]
    [InlineData(WindowState.Maximized, WindowState.Maximized, false)]
    [InlineData(WindowState.Normal, WindowState.FullScreen, false)]
    [InlineData(WindowState.Minimized, WindowState.Normal, false)]
    public void OnlyAChangeFromFullscreen_LeavesIt(WindowState old, WindowState state, bool left) =>
        Assert.Equal(left, MainWindow.LeftFullScreen(old, state));

    [Fact]
    public void Fullscreen_IsNeverSavedAsTheWindowsState() => Headless.Run(() =>
    {
        var settings = new AppSettings { WindowMaximized = true };
        var vm = new MainViewModel();
        var window = Show(vm, settings);
        window.WindowState = WindowState.Maximized;
        Settle(window);

        vm.NowPlaying.IsFullscreen = true;
        Settle(window);
        Assert.True(settings.WindowMaximized);

        Press(window, Key.F11);
        Settle(window);
        Press(window, Key.F11);
        Settle(window);
        Assert.True(settings.WindowMaximized);
    });

    [Fact]
    public void KeyboardFocus_DoesntHoldTheControls_AndAKeyBringsThemBack() => Headless.Run(() =>
    {
        var (vm, _, _, timers) = NowPlaying();
        AccessibleNames.Install();
        ShellResources.Ensure();
        var window = new Window { Content = new NowPlayingView { DataContext = vm } };
        window.Show();
        vm.IsFullscreen = true;
        Settle(window);

        var toggle = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FullscreenToggle");
        Assert.True(toggle.Focus(NavigationMethod.Tab));
        timers.Pass(NowPlayingViewModel.ChromeDelay);
        Assert.True(vm.IsChromeHidden);

        Press(window, Key.Tab);
        Assert.True(vm.IsChromeShown);
    });

    [Fact]
    public void ThePointer_HidesWithTheControls_OnlyWhileTheWindowIsActive()
    {
        var (vm, _, _, timers) = NowPlaying();
        vm.IsFullscreen = true;
        timers.Pass(NowPlayingViewModel.ChromeDelay);
        Assert.True(vm.IsChromeHidden);
        Assert.True(vm.IsPointerHidden);

        // A dialog takes the focus: the controls stay hidden, the pointer shows.
        vm.IsWindowActive = false;
        Assert.True(vm.IsChromeHidden);
        Assert.False(vm.IsPointerHidden);

        vm.IsWindowActive = true;
        Assert.True(vm.IsPointerHidden);
    }

    [Fact]
    public void ADialogOverFullscreen_ShowsThePointer_AndLeavesTheControlsHidden() => Headless.Run(() =>
    {
        var (vm, _, _, timers) = NowPlaying();
        AccessibleNames.Install();
        ShellResources.Ensure();
        var view = new NowPlayingView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();
        window.Activate();
        vm.IsFullscreen = true;
        Settle(window);
        timers.Pass(NowPlayingViewModel.ChromeDelay);
        Assert.True(window.IsActive);
        Assert.Equal(StandardCursorType.None, CursorType(view));

        // A headless window doesn't lose the focus to another one, so this is
        // what the platform reports when a dialog takes it.
        Report(window, "Deactivated");
        Settle(window);
        Assert.False(window.IsActive);
        Assert.True(vm.IsChromeHidden);
        Assert.Null(view.Cursor);

        Report(window, "Activated");
        Settle(window);
        Assert.Equal(StandardCursorType.None, CursorType(view));
    });

    /// <summary>Raises the platform window's activation callback, which is
    /// internal to Avalonia.</summary>
    private static void Report(Window window, string callback)
    {
        var property = typeof(Avalonia.Platform.IWindowBaseImpl).GetProperty(callback,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;
        ((Action)property.GetValue(window.PlatformImpl)!)();
    }

    private static StandardCursorType? CursorType(Control control) =>
        control.Cursor?.ToString() is { } name && Enum.TryParse<StandardCursorType>(name, out var type) ? type : null;

    [Fact]
    public void TheFullscreenButtons_HaveNames() => Headless.Run(() =>
    {
        var vm = new MainViewModel();
        vm.Player.NowPlaying = First;
        vm.Player.ShownTrack = First;
        vm.NowPlaying.IsOpen = true;
        var window = Show(vm);

        Assert.Contains(window.GetVisualDescendants().OfType<Button>(),
            b => AutomationProperties.GetName(b) == "Fullscreen visualizer" && b.IsEffectivelyVisible);

        vm.NowPlaying.IsFullscreen = true;
        Settle(window);
        var shown = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.FindAncestorOfType<NowPlayingView>() is not null).ToList();
        Assert.Contains(shown, b => AutomationProperties.GetName(b) == "Leave fullscreen (Esc)");
        Assert.DoesNotContain(shown, b => AutomationProperties.GetName(b) == "Collapse");
        Assert.All(shown, b => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(b))));
    });
}
