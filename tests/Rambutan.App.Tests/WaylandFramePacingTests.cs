using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Rambutan.App.Services;

namespace Rambutan.App.Tests;

/// <summary>The Wayland render loop's pacing reaches Avalonia.Wayland's
/// internals by name. Off Wayland it stays out of the way; on an Avalonia that
/// renamed those internals it would quietly stop pacing, so the names are pinned here
/// and a package update that breaks them fails the build instead.</summary>
public class WaylandFramePacingTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Assembly Wayland = typeof(WaylandPlatformOptions).Assembly;

    [Fact]
    public void OffWayland_LeavesTheRenderLoopAlone() => Headless.Run(() =>
    {
        var window = new Window();
        window.Show();

        Assert.False(WaylandFramePacing.Install(window));
    });

    [Fact]
    public void RenderLoop_StillHasItsWakeupCallback()
    {
        var loop = Wayland.GetType(WaylandFramePacing.RenderLoopType)!;
        Assert.NotNull(loop);
        Assert.Equal(typeof(Action), loop.GetField("WakeupCallback", Instance)?.FieldType);
    }

    [Fact]
    public void WindowSurface_StillReportsAPendingFrameCallback()
    {
        var window = Wayland.GetType(WaylandFramePacing.WindowImplType)!;
        var proxy = window.GetProperty("SurfaceProxy", Instance)?.PropertyType;
        Assert.Equal("Avalonia.Wayland.Server.Persistent.WXdgShellSurfaceProxy", proxy?.FullName);

        // The accessor reads the base proxy's target; WXdgShellSurfaceProxy hides it with a narrower one.
        var baseProxy = Wayland.GetType("Avalonia.Wayland.Server.Persistent.WSurfaceProxy");
        Assert.True(baseProxy?.IsAssignableFrom(proxy));
        var target = baseProxy!.GetProperty("ProxyTarget", Instance | BindingFlags.DeclaredOnly)?.PropertyType;
        Assert.Equal("Avalonia.Wayland.Server.Persistent.IWSurface", target?.FullName);

        var surface = Wayland.GetType(WaylandFramePacing.SurfaceType)!;
        Assert.True(target!.IsAssignableFrom(surface));
        Assert.Equal(typeof(bool), surface.GetProperty("IsFrameCallbackPending", Instance)?.PropertyType);
    }

    [Fact]
    public void Compositor_StillExposesItsLoop() =>
        Assert.Equal(
            "Avalonia.Rendering.IRenderLoop",
            typeof(Avalonia.Rendering.Composition.Compositor).GetProperty("Loop", Instance)?.PropertyType.FullName);
}
