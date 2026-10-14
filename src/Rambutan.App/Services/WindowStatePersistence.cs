using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace Rambutan.App.Services;

/// <summary>
/// Restores the main window's bounds and maximized state from settings and keeps
/// the settings object current while the window moves; disk persistence rides on
/// the app's normal settings save. The full window and the mini player keep
/// bounds of their own: <see cref="IsMini"/> says which ones the window's moves
/// write, and <see cref="Paused"/> stops writes while the window changes mode, so
/// the sizes it passes through land in neither.
/// </summary>
public sealed class WindowStatePersistence
{
    private readonly Window _window;
    private readonly AppSettings _settings;

    private WindowStatePersistence(Window window, AppSettings settings)
    {
        _window = window;
        _settings = settings;
    }

    public bool IsMini { get; set; }

    public bool Paused { get; set; }

    public static WindowStatePersistence Attach(Window window, AppSettings settings)
    {
        var persistence = new WindowStatePersistence(window, settings);
        persistence.RestoreFull();

        window.PositionChanged += (_, e) =>
        {
            if (persistence.Paused || window.WindowState != WindowState.Normal)
                return;
            if (persistence.IsMini)
            {
                settings.MiniWindowX = e.Point.X;
                settings.MiniWindowY = e.Point.Y;
            }
            else
            {
                settings.WindowX = e.Point.X;
                settings.WindowY = e.Point.Y;
            }
        };

        window.SizeChanged += (_, e) =>
        {
            if (persistence.Paused || window.WindowState != WindowState.Normal)
                return;
            if (persistence.IsMini)
            {
                settings.MiniWindowWidth = e.NewSize.Width;
            }
            else
            {
                settings.WindowWidth = e.NewSize.Width;
                settings.WindowHeight = e.NewSize.Height;
            }
        };

        window.PropertyChanged += (_, e) =>
        {
            // Minimize is transient; only Normal/Maximized are worth restoring.
            // The mini player is never maximized, so it leaves the full window's state alone.
            if (e.Property == Window.WindowStateProperty && !persistence.Paused && !persistence.IsMini &&
                e.NewValue is WindowState state && state != WindowState.Minimized)
            {
                settings.WindowMaximized = state == WindowState.Maximized;
            }
        };

        return persistence;
    }

    /// <summary>Puts the window back at the full window's bounds, maximized if it was.</summary>
    public void RestoreFull()
    {
        if (_settings is { WindowWidth: >= 400 and var width, WindowHeight: >= 300 and var height })
        {
            _window.Width = width;
            _window.Height = height;
        }

        if (_settings is { WindowX: { } x, WindowY: { } y })
            MoveIfVisible(x, y);

        if (_settings.WindowMaximized)
            _window.WindowState = WindowState.Maximized;
    }

    /// <summary>Puts the window at the mini player's last width and position.
    /// The first time it has none, it keeps the position it has; false says the
    /// width is the caller's to pick.</summary>
    public bool RestoreMini()
    {
        if (_settings is { MiniWindowX: { } x, MiniWindowY: { } y })
            MoveIfVisible(x, y);
        if (_settings.MiniWindowWidth is not (>= 200 and var width))
            return false;
        _window.Width = width;
        return true;
    }

    private void MoveIfVisible(int x, int y)
    {
        // A monitor may have been unplugged since the last run; only restore
        // a position whose top-left region is still visible somewhere.
        var probe = new PixelRect(new PixelPoint(x, y), new PixelSize(120, 40));
        if (_window.Screens.All.Any(s => s.Bounds.Intersects(probe)))
            _window.Position = new PixelPoint(x, y);
    }
}
