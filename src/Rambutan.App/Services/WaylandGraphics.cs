using System;
using System.IO;

namespace Rambutan.App.Services;

/// <summary>
/// Which swapchain the native Wayland backend renders through. Avalonia's
/// default, the standard WSI path (<c>wl_egl_window</c>), gets Mesa's EGL from
/// glvnd on NVIDIA's proprietary driver, and Mesa has no driver for that GPU, so
/// the window is drawn in software by llvmpipe: about 140% CPU while a demo
/// track played, against 34% on the GPU. The dmabuf swapchain allocates
/// through GBM on the compositor's device, which NVIDIA's driver supports, and
/// renders on the GPU. Every other driver keeps Avalonia's choice.
/// </summary>
public static class WaylandGraphics
{
    /// <summary>Present while NVIDIA's kernel module (open or closed) is loaded.</summary>
    internal const string NvidiaDriverFile = "/proc/driver/nvidia/version";

    /// <summary>For <c>WaylandPlatformOptions.UseDmabufSwapchain</c>: true with
    /// NVIDIA's driver, otherwise null (Avalonia decides).</summary>
    public static bool? DmabufSwapchain() => DmabufSwapchain(File.Exists);

    internal static bool? DmabufSwapchain(Func<string, bool> fileExists) =>
        fileExists(NvidiaDriverFile) ? true : null;
}
