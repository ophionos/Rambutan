using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using NWayland.Protocols.XdgShell;
using Rambutan.App.Services;

namespace Rambutan.App.Tests;

/// <summary>The Wayland window's app id and raising reach Avalonia.Wayland's
/// internals by name. The names are pinned here, so a package update that renames
/// them fails the build instead of quietly bringing the generic dock icon back.</summary>
public class WaylandToplevelTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Assembly Wayland = typeof(WaylandPlatformOptions).Assembly;

    [Fact]
    public void OffWayland_LeavesTheWindowAlone() => Headless.Run(() =>
    {
        var window = new Window();
        window.Show();

        Assert.False(WaylandToplevel.IsWayland(window));
        Assert.False(WaylandToplevel.Install(window));
    });

    [Fact]
    public void AppId_NamesTheDesktopFile() =>
        Assert.Equal(Rambutan.Integrations.Linux.LinuxAppEntry.DesktopFile, WaylandToplevel.AppId + ".desktop");

    [Fact]
    public void Window_StillReachesItsWorkerClient()
    {
        var windowBase = Wayland.GetType(WaylandToplevel.WindowBaseImplType)!;
        Assert.True(windowBase.IsAssignableFrom(Wayland.GetType(WaylandFramePacing.WindowImplType)));
        var client = windowBase.GetProperty("Client", Instance | BindingFlags.DeclaredOnly)?.PropertyType;
        Assert.Equal(WaylandToplevel.WorkerClientType, client?.FullName);

        var post = client!.GetMethod("PostOob", Instance, [typeof(Action)]);
        Assert.Equal(typeof(void), post?.ReturnType);
    }

    [Fact]
    public void Window_StillReportsTheCompositorsState()
    {
        var window = Wayland.GetType(WaylandFramePacing.WindowImplType)!;
        Assert.Equal(typeof(WindowState), window.GetProperty("WindowState", Instance | BindingFlags.DeclaredOnly)?.PropertyType);
    }

    [Fact]
    public void TopLevel_StillKeepsItsXdgToplevel()
    {
        var topLevel = Wayland.GetType(WaylandToplevel.TopLevelType)!;
        Assert.True(Wayland.GetType("Avalonia.Wayland.Server.Persistent.IWSurface")!.IsAssignableFrom(topLevel));
        Assert.Equal(typeof(XdgToplevel), topLevel.GetField("_xdgTopLevel", Instance)?.FieldType);
        Assert.NotNull(typeof(XdgToplevel).GetMethod(nameof(XdgToplevel.SetAppId), [typeof(string)]));
    }
}
