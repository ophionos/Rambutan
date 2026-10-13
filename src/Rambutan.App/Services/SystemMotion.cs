using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;

namespace Rambutan.App.Services;

/// <summary>
/// Reads the operating system's "reduce motion" setting. Avalonia
/// 12 doesn't surface one, so it's read per platform:
/// <list type="bullet">
/// <item>Windows: "Show animations in Windows" (SPI_GETCLIENTAREAANIMATION), the
/// switch Settings → Accessibility → Visual effects flips. Cheap, so it's read
/// again whenever the window is activated, which catches a change made meanwhile.</item>
/// <item>Linux: GNOME's <c>org.gnome.desktop.interface enable-animations</c>, which
/// GNOME, Cinnamon and Budgie honour, read with <c>gsettings</c> off the UI
/// thread. Spawning a process on every activation would cost more than it's worth,
/// so this is read once per start.</item>
/// </list>
/// Anything unreadable (no gsettings, another desktop) counts as "not reduced":
/// the OS setting is an opt-in, and the in-app setting can still force it.
/// </summary>
public static class SystemMotion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    /// <summary>True, false, or null when this platform's setting can't be read.</summary>
    public static async Task<bool?> ReadAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return ReadWindows();
            if (OperatingSystem.IsLinux())
                return await Task.Run(ReadGnome);
        }
        catch (Exception ex)
        {
            AppLog.Write("motion", $"Couldn't read the system motion setting: {ex.Message}");
        }
        return null;
    }

    /// <summary>The Windows read alone, for re-checks on window activation (it's
    /// a single user32 call). Null elsewhere.</summary>
    public static bool? ReadWindowsNow()
    {
        try
        {
            return OperatingSystem.IsWindows() ? ReadWindows() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool? ReadWindows() =>
        SystemParametersInfoW(SpiGetClientAreaAnimation, 0, out var enabled, 0) != 0
            ? enabled == 0
            : null;

    private static bool? ReadGnome()
    {
        var start = new ProcessStartInfo("gsettings", "get org.gnome.desktop.interface enable-animations")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // no gsettings: not a GNOME-family desktop
        }
        if (process is null)
            return null;

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(2000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            return ParseGnome(output.Result);
        }
    }

    /// <summary>gsettings prints "true" or "false"; anything else is unknown.</summary>
    public static bool? ParseGnome(string output) => output.Trim() switch
    {
        "false" => true,  // animations disabled: reduce motion
        "true" => false,
        _ => null,
    };

    // Blittable signature (out int, not out bool), so no marshalling stub is
    // generated and NativeAOT has nothing to warn about.
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SystemParametersInfoW(uint action, uint param, out int value, uint winIni);
}
