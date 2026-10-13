using System;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using NWayland.Protocols.XdgShell;
using Rambutan.Core.Diagnostics;
using Rambutan.Integrations.Linux;

namespace Rambutan.App.Services;

/// <summary>
/// What Avalonia 12.1's native Wayland backend leaves out of a window.
///
/// <para>It never sets the toplevel's app id, so a dock can't match the window to
/// its desktop file and draws a generic icon; COSMIC has no protocol for a window
/// to send its own icon. <see cref="Install"/> sets <see cref="AppId"/> on every
/// toplevel the window gets (each Show makes a new one).</para>
///
/// <para>Its <c>Activate</c> is empty and xdg-shell has no "unminimize", so a
/// minimized or buried window can't be brought back by the app.
/// <see cref="Raise"/> maps the window again instead: compositors put a newly
/// mapped window in front.</para>
///
/// <para>Like <see cref="WaylandFramePacing"/> it reaches into Avalonia.Wayland's
/// internals by name and checks what it finds; on an Avalonia that renamed them
/// the window keeps the generic icon, and the log says so. Remove it once
/// Avalonia sets an app id and activates windows itself.</para>
/// </summary>
public static class WaylandToplevel
{
    /// <summary>The toplevel's app id; <see cref="LinuxAppEntry"/> writes the
    /// desktop file that carries the icon.</summary>
    public const string AppId = LinuxAppEntry.AppId;

    private const string Wayland = ", Avalonia.Wayland";
    internal const string WindowBaseImplType = "Avalonia.Wayland.WindowBaseImpl";
    internal const string WorkerClientType = "Avalonia.Wayland.Server.WaylandWorkerClient";
    internal const string TopLevelType = "Avalonia.Wayland.Server.Persistent.WXdgTopLevel";

    /// <summary>Whether <paramref name="window"/> runs on Avalonia's native Wayland backend.</summary>
    public static bool IsWayland(Window window) =>
        window.PlatformImpl?.GetType().FullName == WaylandFramePacing.WindowImplType;

    /// <summary>Sets the app id each time <paramref name="window"/> is shown; does
    /// nothing off Wayland.</summary>
    /// <returns>Whether the window will get its app id.</returns>
    public static bool Install(Window window)
    {
        if (!IsWayland(window))
            return false;

        window.PropertyChanged += (_, e) =>
        {
            // Show raises IsVisible before the platform window makes its toplevel.
            if (e.Property == Avalonia.Visual.IsVisibleProperty && window.IsVisible)
                Dispatcher.UIThread.Post(() => SetAppId(window));
        };
        if (window.IsVisible)
            SetAppId(window);
        return true;
    }

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

    private static void SetAppId(Window window)
    {
        try
        {
            if (window.PlatformImpl is not { } impl || impl.GetType().FullName != WaylandFramePacing.WindowImplType
                || Client(impl) is not { } client || client.GetType().FullName != WorkerClientType
                || WaylandFramePacing.CurrentSurface(impl) is not { } surface || surface.GetType().FullName != TopLevelType)
            {
                AppLog.Write("render", "Wayland app id not set: the window isn't the one expected");
                return;
            }

            // The toplevel's requests belong to the Wayland thread.
            PostOob(client, () =>
            {
                try
                {
                    XdgToplevel(surface)?.SetAppId(AppId);
                }
                catch (ObjectDisposedException)
                {
                    // Hidden again before this ran; the next Show sets it.
                }
            });
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NotSupportedException)
        {
            AppLog.Write("render", $"Wayland app id not set: {e.GetType().Name}: {e.Message}");
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_WindowState")]
    private static extern WindowState ImplWindowState(
        [UnsafeAccessorType(WaylandFramePacing.WindowImplType + Wayland)] object window);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Client")]
    [return: UnsafeAccessorType(WorkerClientType + Wayland)]
    private static extern object? Client([UnsafeAccessorType(WindowBaseImplType + Wayland)] object window);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "PostOob")]
    private static extern void PostOob([UnsafeAccessorType(WorkerClientType + Wayland)] object client, Action callback);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_xdgTopLevel")]
    private static extern ref XdgToplevel? XdgToplevel([UnsafeAccessorType(TopLevelType + Wayland)] object topLevel);
}
