using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Rambutan.Integrations.Linux;

namespace Rambutan.App.Services;

/// <summary>
/// What Avalonia 12.1's native Wayland backend leaves out of a window.
///
/// <para>Its <c>Activate</c> is empty and xdg-shell has no "unminimize", so a
/// minimized or buried window can't be brought back by the app.
/// <see cref="Raise"/> maps the window again instead: compositors put a newly
/// mapped window in front.</para>
///
/// <para>Like <see cref="WaylandFramePacing"/> it reaches into Avalonia.Wayland's
/// internals by name; <c>WaylandToplevelTests</c> pins the name. Remove it once
/// Avalonia activates windows itself.</para>
/// </summary>
public static class WaylandToplevel
{
    /// <summary>The toplevel's app id, set for every window through
    /// <c>WaylandPlatformOptions.AppId</c>; <see cref="LinuxAppEntry"/> writes the
    /// desktop file that carries the icon.</summary>
    public const string AppId = LinuxAppEntry.AppId;

    private const string Wayland = ", Avalonia.Wayland";

    /// <summary>Whether <paramref name="window"/> runs on Avalonia's native Wayland backend.</summary>
    public static bool IsWayland(Window window) =>
        window.PlatformImpl?.GetType().FullName == WaylandFramePacing.WindowImplType;

    /// <summary>Brings the window to the front: shows it when hidden, and maps it
    /// again when it is minimized or behind other windows, keeping it maximized
    /// or full screen if it was.</summary>
    public static void Raise(Window window)
    {
        if (window.IsVisible && window.IsActive)
            return;

        // The platform window's state is the compositor's; Window.WindowState
        // still says Minimized after a minimize, and a new toplevel would apply it.
        var state = window.PlatformImpl is { } impl ? ImplWindowState(impl) : WindowState.Normal;
        if (state == WindowState.Minimized)
            state = WindowState.Normal;
        if (window.IsVisible)
            window.Hide();
        window.WindowState = WindowState.Normal;
        window.Show();
        // The new toplevel starts out normal.
        if (state != WindowState.Normal)
            window.WindowState = state;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_WindowState")]
    private static extern WindowState ImplWindowState(
        [UnsafeAccessorType(WaylandFramePacing.WindowImplType + Wayland)] object window);
}
