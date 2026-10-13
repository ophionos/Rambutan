using System;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Rambutan.Core.Diagnostics;

namespace Rambutan.App.Services;

/// <summary>
/// Paces Avalonia's native Wayland render loop on the window's frame callbacks.
/// Avalonia 12.1's loop ticks on a frame callback, on a 20 fps starvation
/// timer, and on every UI-thread commit: <c>ServerCompositor.EnqueueBatch</c>
/// wakes it straight away, even while the window still waits for its frame
/// callback. A UI-thread animation that redraws each frame (the karaoke sweep, the
/// lyrics scroll, any Avalonia animation or transition) then commits again as soon
/// as its last batch is processed, and the two threads ping-pong: about 12,000
/// frames a second on Now Playing with karaoke, where X11 holds the refresh rate.
///
/// <para>This wraps the loop's wakeup: a commit wakes the loop at once only while
/// the main window isn't waiting for a frame callback. Otherwise the batch waits
/// for that callback, which ticks the loop anyway, or for Avalonia's starvation
/// timer when the compositor holds callbacks back (a hidden window), as the
/// backend's design intends. Other windows and popups share the loop, so their
/// commits wait at most for the main window's next frame.</para>
///
/// <para>It reaches into Avalonia.Wayland's internals by name, so every step checks
/// what it finds and leaves the loop alone when anything differs: an Avalonia that
/// renamed something renders unpaced, as before, and the log says so. Remove it
/// once Avalonia paces the loop itself.</para>
/// </summary>
public static class WaylandFramePacing
{
    private const string Wayland = ", Avalonia.Wayland";
    internal const string RenderLoopType = "Avalonia.Wayland.Server.WaylandWorker+RenderLoopImpl";
    internal const string WindowImplType = "Avalonia.Wayland.WindowImpl";
    internal const string SurfaceType = "Avalonia.Wayland.Server.Persistent.WSurface";

    /// <summary>Wraps the render loop's wakeup when <paramref name="window"/> runs
    /// on Avalonia's native Wayland backend; does nothing on any other.</summary>
    /// <returns>Whether the loop is now paced.</returns>
    public static bool Install(Window window)
    {
        if (window.PlatformImpl?.GetType().FullName != WindowImplType
            || ElementComposition.GetElementVisual(window)?.Compositor is not { } compositor)
            return false;

        try
        {
            if (Loop(compositor) is not { } loop || loop.GetType().FullName != RenderLoopType)
            {
                AppLog.Write("render", "Wayland frame pacing not installed: the render loop isn't the one expected");
                return false;
            }

            ref var wakeup = ref WakeupCallback(loop);
            if (wakeup is not { } original)
            {
                AppLog.Write("render", "Wayland frame pacing not installed: the render loop has no wakeup");
                return false;
            }

            // Read once here, so a renamed member fails now rather than on a commit.
            _ = AwaitsFrame(window);
            wakeup = () =>
            {
                if (!AwaitsFrame(window))
                    original();
            };
            return true;
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NotSupportedException)
        {
            AppLog.Write("render", $"Wayland frame pacing not installed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>Whether the window's surface waits for its frame callback, which
    /// will tick the loop when it comes. A window without a surface (hidden, or
    /// between surfaces) doesn't.</summary>
    private static bool AwaitsFrame(Window window) =>
        window.PlatformImpl is { } impl && impl.GetType().FullName == WindowImplType
        && CurrentSurface(impl) is { } surface && IsSurface(surface)
        && IsFrameCallbackPending(surface);

    /// <summary>The surface behind a Wayland window's platform window, or null
    /// while it has none (hidden, or between surfaces).</summary>
    internal static object? CurrentSurface(object windowImpl) =>
        SurfaceProxy(windowImpl) is { } proxy ? ProxyTarget(proxy) : null;

    private static bool IsSurface(object item)
    {
        for (var type = item.GetType(); type is not null; type = type.BaseType)
        {
            if (type.FullName == SurfaceType)
                return true;
        }
        return false;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Loop")]
    [return: UnsafeAccessorType("Avalonia.Rendering.IRenderLoop, Avalonia.Base")]
    private static extern object? Loop(Compositor compositor);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "WakeupCallback")]
    private static extern ref Action? WakeupCallback([UnsafeAccessorType(RenderLoopType + Wayland)] object loop);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SurfaceProxy")]
    [return: UnsafeAccessorType("Avalonia.Wayland.Server.Persistent.WXdgShellSurfaceProxy" + Wayland)]
    private static extern object? SurfaceProxy([UnsafeAccessorType(WindowImplType + Wayland)] object window);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ProxyTarget")]
    [return: UnsafeAccessorType("Avalonia.Wayland.Server.Persistent.IWSurface" + Wayland)]
    private static extern object? ProxyTarget(
        [UnsafeAccessorType("Avalonia.Wayland.Server.Persistent.WSurfaceProxy" + Wayland)] object proxy);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IsFrameCallbackPending")]
    private static extern bool IsFrameCallbackPending([UnsafeAccessorType(SurfaceType + Wayland)] object surface);
}
