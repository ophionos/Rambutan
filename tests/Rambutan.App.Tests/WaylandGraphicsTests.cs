using Rambutan.App.Services;

namespace Rambutan.App.Tests;

/// <summary>NVIDIA's driver gets the dmabuf swapchain; every other driver keeps
/// Avalonia's choice.</summary>
public class WaylandGraphicsTests
{
    [Fact]
    public void NvidiasDriver_GetsTheDmabufSwapchain() =>
        Assert.True(WaylandGraphics.DmabufSwapchain(path => path == WaylandGraphics.NvidiaDriverFile));

    [Fact]
    public void OtherDrivers_LeaveItToAvalonia() =>
        Assert.Null(WaylandGraphics.DmabufSwapchain(_ => false));
}
