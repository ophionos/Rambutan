#if WINDOWS
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Playback;
using Rambutan.Integrations.Taskbar;

namespace Rambutan.App.Services;

/// <summary>
/// The main window's buttons under its taskbar preview and the progress bar on
/// its taskbar button. They follow what the player bar shows and act as its
/// buttons do: Like goes through the same command, so its outcome toasts the
/// same way. Everything here runs on the UI thread.
/// </summary>
internal sealed class TaskbarControls : IDisposable
{
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_DPICHANGED = 0x02E0;

    private readonly Window _window;
    private readonly TaskbarThumbnailToolbar _toolbar;
    private readonly PlaybackCoordinator _coordinator;
    private readonly TrackRatings _ratings;
    private readonly SessionViewModel _session;
    private readonly TrackActionsViewModel _actions;
    private readonly TaskbarProgressGate _gate = new();
    private readonly Win32Properties.CustomWndProcHookCallback _hook;
    private TaskbarButtonView[]? _shown;
    private PlaybackProgress? _progress;
    private bool _on;
    private bool _disposed;

    private TaskbarControls(
        Window window, nint hwnd, bool on, PlaybackCoordinator coordinator, TrackRatings ratings,
        SessionViewModel session, TrackActionsViewModel actions)
    {
        _window = window;
        _on = on;
        _coordinator = coordinator;
        _ratings = ratings;
        _session = session;
        _actions = actions;
        _toolbar = new TaskbarThumbnailToolbar(hwnd, Strings.Get);
        _toolbar.ButtonClicked += button => Dispatcher.UIThread.Post(() => Act(button));
        _hook = OnMessage;
    }

    /// <summary>Null when the window has no handle to attach to.</summary>
    public static TaskbarControls? Attach(
        Window window, bool on, PlaybackCoordinator coordinator, TrackRatings ratings,
        SessionViewModel session, TrackActionsViewModel actions)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0)
            return null;
        var controls = new TaskbarControls(window, hwnd, on, coordinator, ratings, session, actions);
        controls.Start();
        return controls;
    }

    private void Start()
    {
        Win32Properties.AddWndProcHookCallback(_window, _hook);
        RenderIcons();

        // Coordinator, queue and rating events come on any thread.
        var queue = _coordinator.Queue;
        _coordinator.StateChanged += _ => Dispatcher.UIThread.Post(ShowButtons);
        _coordinator.NowPlayingChanged += _ => Dispatcher.UIThread.Post(() =>
        {
            _progress = null;
            ShowButtons();
            ShowProgress();
        });
        _coordinator.ProgressChanged += progress => Dispatcher.UIThread.Post(() =>
        {
            _progress = progress;
            ShowProgress();
        });
        queue.Changed += () => Dispatcher.UIThread.Post(ShowButtons);
        _ratings.Changed += _ => Dispatcher.UIThread.Post(ShowButtons);
        _session.PropertyChanged += OnSessionChanged;
        Strings.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            _shown = null; // same flags, new tips
            ShowButtons();
        });
        ShowButtons();
        ShowProgress();
    }

    /// <summary>The setting changed.</summary>
    public void SetOn(bool on)
    {
        _on = on;
        ShowButtons();
        ShowProgress();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsSignedIn))
            Dispatcher.UIThread.Post(ShowButtons);
    }

    private void ShowButtons()
    {
        if (_disposed)
            return;
        var queue = _coordinator.Queue;
        var track = _coordinator.NowPlaying ?? queue.Current;
        var buttons = TaskbarToolbarState.Buttons(new TaskbarInput(
            _on,
            queue.Items.Count > 0,
            _coordinator.State is PlaybackState.Playing or PlaybackState.Buffering,
            track,
            track is null ? null : _ratings.Of(track),
            _session.IsSignedIn,
            queue.Shuffle));
        if (_shown is not null && _shown.SequenceEqual(buttons))
            return;
        _shown = buttons;
        _toolbar.SetButtons(buttons);
    }

    private void ShowProgress()
    {
        if (_disposed)
            return;
        var value = TaskbarToolbarState.Progress(
            _on, _coordinator.NowPlaying,
            _progress?.Position ?? TimeSpan.Zero, _progress?.Duration);
        if (_gate.Next(value) is { } changed)
            _toolbar.SetProgress(changed);
    }

    private void Act(TaskbarButton button)
    {
        var queue = _coordinator.Queue;
        switch (button)
        {
            case TaskbarButton.Previous:
                _ = _coordinator.PreviousAsync();
                break;
            case TaskbarButton.PlayPause:
                _coordinator.TogglePlayPause();
                break;
            case TaskbarButton.Next:
                _ = _coordinator.NextAsync();
                break;
            case TaskbarButton.Like:
                if ((_coordinator.NowPlaying ?? queue.Current) is { } track && _actions.LikeCommand.CanExecute(track))
                    _actions.LikeCommand.Execute(track);
                break;
            case TaskbarButton.Shuffle:
                // As the OS media session does: without the player bar's toast,
                // which tells someone looking at the app what happened to the queue.
                queue.SetShuffle(!queue.Shuffle);
                break;
        }
    }

    /// <summary>Draws the icons for the taskbar's current mode and scale.</summary>
    private void RenderIcons()
    {
        var color = TaskbarThumbnailToolbar.UsesLightTheme() ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Colors.White;
        var icons = TaskbarGlyphs.RenderAll(
            key => _window.TryFindResource(key, out var value) ? value as Geometry : null,
            TaskbarThumbnailToolbar.IconSize, color);
        _toolbar.SetIcons(icons);
    }

    private nint OnMessage(nint hwnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_toolbar.HandleMessage(msg, wParam))
        {
            handled = true;
            return 0;
        }
        // The taskbar's mode or the display's scale changed.
        if ((msg == WM_SETTINGCHANGE && lParam != 0 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            || msg == WM_DPICHANGED)
        {
            Dispatcher.UIThread.Post(RenderIcons);
        }
        return 0;
    }

    public void Dispose()
    {
        _disposed = true;
        Win32Properties.RemoveWndProcHookCallback(_window, _hook);
        _session.PropertyChanged -= OnSessionChanged;
        _toolbar.Dispose();
    }
}
#endif
