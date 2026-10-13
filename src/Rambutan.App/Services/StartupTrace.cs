using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Rambutan.App.Services;

/// <summary>
/// Startup instrumentation: writes milliseconds from OS process start to the first
/// idle UI frame into the file named by RAMBUTAN_STARTUP_TRACE, then exits if
/// RAMBUTAN_STARTUP_EXIT is set. Inert without the variable, so it costs a null
/// check in normal runs.
/// </summary>
public static class StartupTrace
{
    public static void Attach(Window window)
    {
        if (Environment.GetEnvironmentVariable("RAMBUTAN_STARTUP_TRACE") is not { } path)
            return;

        window.Opened += (_, _) =>
            // Background priority runs after this frame's layout and render work,
            // so the shell is genuinely on screen and idle when this fires.
            Dispatcher.UIThread.Post(() =>
            {
                var elapsed = DateTime.Now - Process.GetCurrentProcess().StartTime;
                File.WriteAllText(path,
                    elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture));

                if (Environment.GetEnvironmentVariable("RAMBUTAN_STARTUP_EXIT") is not null)
                    Environment.Exit(0);
            }, DispatcherPriority.Background);
    }
}
