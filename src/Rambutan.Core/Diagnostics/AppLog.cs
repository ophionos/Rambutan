namespace Rambutan.Core.Diagnostics;

/// <summary>
/// Process-wide diagnostic sink for field evidence (playback failures, resolution
/// retries, sidecar updates). Core defines only the port — zero I/O here; the app
/// installs a file-backed sink at startup. With no sink installed, writes vanish,
/// so libraries log unconditionally without null checks or test setup.
/// </summary>
public static class AppLog
{
    /// <summary>(category, message) → wherever the host wants it. Must be thread-safe.</summary>
    public static Action<string, string>? Sink { get; set; }

    public static void Write(string category, string message) => Sink?.Invoke(category, message);
}
