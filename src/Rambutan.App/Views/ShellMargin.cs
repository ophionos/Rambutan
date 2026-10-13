using System;
using Avalonia;

namespace Rambutan.App.Views;

/// <summary>
/// Keeps the shell inside the window's visible frame. The window extends its
/// content into the decorations and draws its own title bar, so where Avalonia
/// draws the decorations itself (Wayland, X11) the shell must stay clear of the
/// frame and shadow around the window, as the caption buttons do; otherwise it
/// starts in the invisible shadow and the title bar reads several pixels above
/// the buttons. <see cref="Window.WindowDecorationMargin"/> holds those edges plus
/// the title bar's height on top, which the shell fills itself.
/// <see cref="Window.OffScreenMargin"/> is the frame Windows hides off-screen
/// while maximized.
/// </summary>
internal static class ShellMargin
{
    public static Thickness For(Thickness offScreen, Thickness decoration, double titleBarHeight)
    {
        // Platform decorations (Windows) report only the title bar: no edges to keep.
        var drawn = decoration.Left > 0 || decoration.Right > 0 || decoration.Bottom > 0;
        var top = drawn ? Math.Max(0, decoration.Top - titleBarHeight) : 0;
        return new Thickness(
            offScreen.Left + (drawn ? decoration.Left : 0),
            offScreen.Top + top,
            offScreen.Right + (drawn ? decoration.Right : 0),
            offScreen.Bottom + (drawn ? decoration.Bottom : 0));
    }
}
