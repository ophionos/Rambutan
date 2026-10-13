using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Playback;

namespace Rambutan.App.Services;

/// <summary>
/// <see cref="IStreamSource"/> decorator that holds resolution until the sidecars
/// are ready.
///
/// Browsing works from the first frame, so playback can be asked for before ffmpeg has been
/// initialized. Waiting here rather than failing keeps that a slower first play
/// instead of "FfmpegRuntime.Initialize must be called first". Disk-cache hits go
/// through the gate too: a local file still needs ffmpeg to decode.
/// </summary>
public sealed class SidecarGate(IStreamSource inner, SidecarBootstrapper bootstrapper)
    : IStreamSource, IStreamWarmer, IOfflineAvailability
{
    // Hover warm-up passes through when the source can warm; one that can't
    // is always "warm", so the scheduler never asks it.
    public bool IsWarm(string videoId) => inner is not IStreamWarmer warmer || warmer.IsWarm(videoId);

    public async Task WarmAsync(string videoId, CancellationToken ct)
    {
        if (inner is not IStreamWarmer warmer)
            return;
        // yt-dlp may still be downloading on a first run; warming waits like a play.
        await bootstrapper.Ready.WaitAsync(ct);
        await warmer.WarmAsync(videoId, ct);
    }

    public async Task<ResolvedMedia> ResolveAsync(
        string videoId, bool bypassCache = false, CancellationToken ct = default)
    {
        // WaitAsync, not a plain await: cancelling this track's load must not
        // cancel the shared preparation every other track is waiting on.
        await bootstrapper.Ready.WaitAsync(ct);
        return await inner.ResolveAsync(videoId, bypassCache, ct);
    }

    public bool IsAvailableOffline(string videoId) =>
        inner is IOfflineAvailability offline && offline.IsAvailableOffline(videoId);
}
