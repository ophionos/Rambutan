using Rambutan.Core.Diagnostics;
using Rambutan.Core.Playback;

namespace Rambutan.Streams;

/// <summary>
/// <see cref="IStreamSource"/> decorator that tells "offline" apart from every other
/// resolution failure, and says so quickly. While the inner source resolves, a
/// connectivity check runs beside it: when the check finds no network, the
/// resolution is abandoned at once with <see cref="OfflineException"/>, instead of
/// after yt-dlp's own retries. When the resolution fails first, the check decides
/// whether the failure was the network's.
/// <para>
/// A slow check that times out never cuts a resolution short, because a slow
/// network looks the same; it only counts once the resolution has failed too.
/// An answer the inner source has already (a cache hit) costs no check.
/// </para>
/// </summary>
public sealed class OfflineAwareSource(
    IStreamSource inner, Func<CancellationToken, Task<Connectivity>> check)
    : IStreamSource, IStreamWarmer, IOfflineAvailability
{
    public async Task<ResolvedMedia> ResolveAsync(
        string videoId, bool bypassCache = false, CancellationToken ct = default)
    {
        try
        {
            return await GuardAsync(videoId, token => inner.ResolveAsync(videoId, bypassCache, token), ct);
        }
        catch (OfflineException) when (
            !bypassCache && inner is ILocalFallback local && local.TryLocal(videoId) is { } media)
        {
            // A local copy below the quality preference: worse than the stream,
            // better than nothing while the network is down.
            AppLog.Write("offline", $"{videoId}: no network, playing the local copy");
            return media;
        }
    }

    public bool IsWarm(string videoId) => inner is not IStreamWarmer warmer || warmer.IsWarm(videoId);

    // Offline, a warm-up would keep a yt-dlp run busy for nothing; the guard ends it.
    public Task WarmAsync(string videoId, CancellationToken ct) =>
        inner is IStreamWarmer warmer
            ? GuardAsync(videoId, async token =>
            {
                await warmer.WarmAsync(videoId, token);
                return true;
            }, ct)
            : Task.CompletedTask;

    public bool IsAvailableOffline(string videoId) =>
        inner is IOfflineAvailability offline && offline.IsAvailableOffline(videoId);

    private async Task<T> GuardAsync<T>(
        string videoId, Func<CancellationToken, Task<T>> run, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var work = run(cts.Token);
        if (work.IsCompleted)
            return await work;

        var probe = check(cts.Token);
        try
        {
            if (await Task.WhenAny(work, probe) == probe &&
                await probe == Connectivity.Offline && !work.IsCompleted)
            {
                throw Offline(videoId, inner: null);
            }

            try
            {
                return await work;
            }
            catch (Exception ex) when (ex is not OfflineException && !ct.IsCancellationRequested)
            {
                if (await probe != Connectivity.Online)
                    throw Offline(videoId, ex);
                throw;
            }
        }
        finally
        {
            // Ends whichever is still running: the resolution when offline was
            // called, the check when the resolution answered first.
            cts.Cancel();
            _ = work.ContinueWith(
                static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            _ = probe.ContinueWith(
                static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    private static OfflineException Offline(string videoId, Exception? inner) =>
        new($"couldn't resolve {videoId}: YouTube Music can't be reached", inner);
}

/// <summary>
/// A source with local copies it skips online (below the quality preference)
/// but would rather play than nothing. <see cref="OfflineAwareSource"/> asks it
/// once a resolution has failed for want of the network.
/// </summary>
internal interface ILocalFallback
{
    ResolvedMedia? TryLocal(string videoId);
}
