using System.Collections.Concurrent;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Playback;
using Rambutan.Core.Settings;

namespace Rambutan.Streams;

/// <summary>
/// IStreamSource adapter: disk cache read-through over the yt-dlp resolver.
/// A cache hit plays the local file and skips resolution entirely; a miss returns
/// the resolved googlevideo URL and fills the cache in the background, so the
/// track is local from its second play onward. Both sides follow the quality
/// preference: an entry worse than it is a miss, and the fill that follows
/// replaces it.
/// Hover warm-up resolves without that fill: nothing is downloaded for a
/// track until it plays. It does check that googlevideo serves the whole file,
/// though, so a warmed play doesn't fail its open. Offline, any entry beats
/// none: see <see cref="TryLocal"/>.
/// </summary>
public sealed class CachingStreamSource(StreamResolver resolver, AudioDiskCache cache)
    : IStreamSource, IStreamWarmer, IOfflineAvailability, ILocalFallback, IDisposable
{
    private readonly HttpClient _http = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    /// <summary>For tests: whether googlevideo cuts a URL off after its start.</summary>
    internal Func<string, CancellationToken, Task<bool>>? CutOffCheck { get; init; }

    public async Task<ResolvedMedia> ResolveAsync(
        string videoId, bool bypassCache = false, CancellationToken ct = default)
    {
        if (bypassCache)
            cache.Remove(videoId); // fault retry: never serve the failing file again
        else if (cache.TryGet(videoId, resolver.Quality) is { } hit)
            return new ResolvedMedia(hit.Path, hit.AudioCodec, hit.LoudnessDb);

        var stream = await resolver.ResolveAsync(videoId, bypassCache, ct);
        _ = FillCacheAsync(stream);
        return new ResolvedMedia(stream.Url, stream.AudioCodec, stream.LoudnessDb);
    }

    public bool IsWarm(string videoId) =>
        cache.Contains(videoId, resolver.Quality) || resolver.IsResolved(videoId);

    /// <summary>
    /// Resolves ahead of a likely play. A URL that googlevideo cuts off after its
    /// start would fail the play's open and cost it a second resolution then, so
    /// the second resolution happens here instead, while nobody waits.
    /// </summary>
    public async Task WarmAsync(string videoId, CancellationToken ct)
    {
        var stream = await resolver.ResolveAsync(videoId, bypassCache: false, ct);
        var cutOff = CutOffCheck is { } check
            ? await check(stream.Url, ct)
            : await CutOffProbe.IsCutOffAsync(_http, stream.Url, ct);
        if (!cutOff)
            return;

        AppLog.Write("streams", $"warm-up URL of {videoId} is cut off after its start; resolving again");
        await resolver.ResolveAsync(videoId, bypassCache: true, ct);
    }

    /// <summary>A cache entry of any quality: what <see cref="TryLocal"/> plays
    /// when the network is down.</summary>
    public bool IsAvailableOffline(string videoId) => cache.Contains(videoId, AudioQuality.DataSaver);

    /// <summary>The best entry whatever the quality preference, High first. Only
    /// asked once resolution failed for want of the network: online, an entry
    /// below the preference stays a miss so the better copy replaces it.</summary>
    public ResolvedMedia? TryLocal(string videoId) =>
        cache.TryGet(videoId, AudioQuality.DataSaver) is { } hit
            ? new ResolvedMedia(hit.Path, hit.AudioCodec, hit.LoudnessDb)
            : null;

    private async Task FillCacheAsync(ResolvedStream stream)
    {
        // Keyed per tier: a Data saver fill still running must not block the High
        // one a quality change asks for.
        var key = $"{stream.VideoId}~{stream.Quality}";
        if (!cache.Enabled || !_inFlight.TryAdd(key, 0))
            return;

        try
        {
            if (cache.Contains(stream.VideoId, stream.Quality))
                return;

            await cache.StoreAsync(
                stream.VideoId, stream.Itag, stream.AudioCodec, stream.Quality,
                (file, ct) => new RangedFetcher(_http).FetchAsync(stream.Url, file, progress: null, ct),
                stream.LoudnessDb, _disposeCts.Token);
        }
        catch (Exception ex)
        {
            // Cache fill is best-effort; playback already streams the URL.
            AppLog.Write("cache", $"fill of {stream.VideoId} failed: {ex.Message}");
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        _http.Dispose();
        _disposeCts.Dispose();
    }
}
