using System.Runtime.InteropServices;
using Rambutan.Core.Diagnostics;

namespace Rambutan.Integrations.Linux;

/// <summary>
/// The name, icon and role PulseAudio and PipeWire show for the app's audio
/// (maintainer, 2026-11-26); without them the mixer shows the binary's name
/// (<c>Rambutan.App</c>). libpulse reads them from <c>PULSE_PROP</c> when
/// miniaudio opens its context.
/// </summary>
public static partial class PulseStreamProperties
{
    public const string Variable = "PULSE_PROP";

    /// <summary>The icon is the one <see cref="LinuxAppEntry"/> installs.</summary>
    public const string Ours =
        "application.name=Rambutan application.icon_name=" + LinuxAppEntry.AppId + " media.role=music";

    /// <summary>What <c>PULSE_PROP</c> should hold: ours, or null to keep a value
    /// the user set themselves.</summary>
    public static string? ValueFor(string? existing) => string.IsNullOrWhiteSpace(existing) ? Ours : null;

    /// <summary>
    /// Sets <c>PULSE_PROP</c> for this process on Linux. Call first in <c>Main</c>,
    /// before any other thread runs: <c>setenv</c> isn't thread-safe. It goes
    /// through libc because <see cref="Environment.SetEnvironmentVariable(string, string)"/>
    /// changes only .NET's own copy, which libpulse never sees.
    /// </summary>
    public static void Apply()
    {
        if (!OperatingSystem.IsLinux() || ValueFor(Environment.GetEnvironmentVariable(Variable)) is not { } value)
            return;
        try
        {
            if (SetEnv(Variable, value, 1) != 0)
                AppLog.Write("audio", $"Couldn't set {Variable} (errno {Marshal.GetLastPInvokeError()}).");
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Write("audio", $"Couldn't set {Variable}: {e.Message}");
        }
    }

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SetEnv(string name, string value, int overwrite);
}
