using System.Diagnostics;

namespace Rambutan.Integrations.Tests;

/// <summary>
/// A <c>dbus-daemon</c> of the test's own, so nothing reaches the session of
/// whoever runs the tests. Skips the test off Linux or without dbus-daemon.
/// </summary>
internal sealed class PrivateBus : IDisposable
{
    private readonly Process _daemon;

    private PrivateBus(Process daemon, string address)
    {
        _daemon = daemon;
        Address = address;
    }

    public string Address { get; }

    public static async Task<PrivateBus> StartAsync()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "D-Bus is Linux only here.");
        Process? daemon = null;
        try
        {
            daemon = Process.Start(new ProcessStartInfo("dbus-daemon", "--session --nofork --print-address=1")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        Assert.SkipWhen(daemon is null, "Needs dbus-daemon.");
        var address = await daemon.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return new PrivateBus(daemon, address!.Trim());
    }

    public void Dispose()
    {
        if (!_daemon.HasExited)
            _daemon.Kill();
        _daemon.Dispose();
    }
}
