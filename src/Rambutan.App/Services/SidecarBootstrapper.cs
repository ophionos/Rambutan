using System;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Audio;
using Rambutan.Core.Diagnostics;
using Rambutan.Streams;
using Rambutan.App.Localization;

namespace Rambutan.App.Services;

/// <summary>
/// First-run/startup preparation: ffmpeg shared libraries (download on Windows),
/// FFmpeg binding initialization, and the yt-dlp binary.
///
/// Nothing in the shell blocks on this — browsing and search need neither sidecar,
/// so the window opens on Home while the download runs behind it. Playback is what
/// waits, via <see cref="SidecarGate"/> awaiting <see cref="Ready"/>.
/// </summary>
/// <param name="needsYtDlp">False in demo mode: its audio is generated, so it never
/// resolves a stream, and installing or updating yt-dlp would only change the
/// shared tools folder behind the installed app's back.</param>
public sealed class SidecarBootstrapper(YtDlpManager ytDlp, FfmpegManager ffmpeg, bool needsYtDlp = true)
{
    private readonly Lock _sync = new();
    private Task? _run;

    /// <summary>
    /// The single preparation run, started on first request. A faulted run is not
    /// cached, so the next caller retries — which is what makes both the status
    /// strip's Retry button and a later press of Play recover on their own.
    /// </summary>
    public Task RunAsync(IProgress<string>? status = null)
    {
        lock (_sync)
        {
            if (_run is { IsFaulted: false })
                return _run;
            return _run = PrepareAsync(status);
        }
    }

    /// <summary>Awaited by anything that actually needs the sidecars (playback).</summary>
    public Task Ready => RunAsync();

    private async Task PrepareAsync(IProgress<string>? status)
    {
        status?.Report(Strings.Get("sidecar.preparingAudio"));
        var ffmpegDir = await ffmpeg.EnsureAsync();
        FfmpegRuntime.Initialize(ffmpegDir);

        if (!needsYtDlp)
        {
            status?.Report(Strings.Get("sidecar.ready"));
            return;
        }

        status?.Report(Strings.Get("sidecar.preparingYtDlp"));
        await ytDlp.EnsureAsync();

        // Staying current IS the extraction countermeasure strategy —
        // yt-dlp goes stale in weeks, not months. Fire-and-forget: startup and
        // playback never wait on it, and a failed update just means today's
        // binary keeps working as well as it did yesterday.
        _ = Task.Run(UpdateYtDlpAsync);

        status?.Report(Strings.Get("sidecar.ready"));
    }

    private async Task UpdateYtDlpAsync()
    {
        try
        {
            var output = await ytDlp.UpdateAsync();
            AppLog.Write("sidecar", $"yt-dlp update: {LastLine(output)}");
        }
        catch (Exception ex)
        {
            AppLog.Write("sidecar", $"yt-dlp update failed: {ex.Message}");
        }
    }

    /// <summary>yt-dlp -U prints progress lines; the verdict is the last one.</summary>
    private static string LastLine(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                       StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : "(no output)";
    }
}
