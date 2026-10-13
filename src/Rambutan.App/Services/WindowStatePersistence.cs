using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace Rambutan.App.Services;

/// <summary>
/// Restores the main window's bounds and maximized state from settings and keeps
/// the settings object current while the window moves; disk persistence rides on
/// the app's normal settings save.
/// </summary>
public static class WindowStatePersistence
{
    public static void Attach(Window window, AppSettings settings)
    {
        Restore(window, settings);

        window.PositionChanged += (_, e) =>
        {
            if (window.WindowState == WindowState.Normal)
            {
                settings.WindowX = e.Point.X;
                settings.WindowY = e.Point.Y;
            }
        };

        window.SizeChanged += (_, e) =>
        {
            if (window.WindowState == WindowState.Normal)
            {
                settings.WindowWidth = e.NewSize.Width;
                settings.WindowHeight = e.NewSize.Height;
            }
        };

        window.PropertyChanged += (_, e) =>
        {
            // Minimize is transient; only Normal/Maximized are worth restoring.
            if (e.Property == Window.WindowStateProperty &&
                e.NewValue is WindowState state && state != WindowState.Minimized)
            {
                settings.WindowMaximized = state == WindowState.Maximized;
            }
        };
    }

    private static void Restore(Window window, AppSettings settings)
    {
        if (settings is { WindowWidth: >= 400 and var width, WindowHeight: >= 300 and var height })
        {
            window.Width = width;
            window.Height = height;
        }

        if (settings is { WindowX: { } x, WindowY: { } y })
        {
            // A monitor may have been unplugged since the last run; only restore
            // a position whose title-bar region is still visible somewhere.
            var probe = new PixelRect(new PixelPoint(x, y), new PixelSize(120, 40));
            if (window.Screens.All.Any(s => s.Bounds.Intersects(probe)))
                window.Position = new PixelPoint(x, y);
        }

        if (settings.WindowMaximized)
            window.WindowState = WindowState.Maximized;
    }
}
