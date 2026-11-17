using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Rambutan.App.Services;

namespace Rambutan.App.Tests;

/// <summary>Raising the Wayland window reaches Avalonia.Wayland's internals by
/// name. The name is pinned here, so a package update that renames it fails the
/// build instead of quietly leaving a minimized window down.</summary>
public class WaylandToplevelTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Assembly Wayland = typeof(WaylandPlatformOptions).Assembly;

    [Fact]
    public void OffWayland_IsNotWayland() => Headless.Run(() =>
    {
        var window = new Window();
        window.Show();

        Assert.False(WaylandToplevel.IsWayland(window));
    });

    [Fact]
    public void AppId_NamesTheDesktopFile() =>
        Assert.Equal(Rambutan.Integrations.Linux.LinuxAppEntry.DesktopFile, WaylandToplevel.AppId + ".desktop");

    [Fact]
    public void WaylandOptions_SetTheAppId() =>
        Assert.Equal(WaylandToplevel.AppId, Program.WaylandOptions().AppId);

    [Fact]
    public void Window_StillReportsTheCompositorsState()
    {
        var window = Wayland.GetType(WaylandFramePacing.WindowImplType)!;
        Assert.Equal(typeof(WindowState), window.GetProperty("WindowState", Instance | BindingFlags.DeclaredOnly)?.PropertyType);
    }
}
