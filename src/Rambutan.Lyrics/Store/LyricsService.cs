using Rambutan.Lyrics.Providers;

namespace Rambutan.Lyrics.Store;

/// <summary>
/// Finds lyrics for a track: the user's custom lyrics first, then the cache,
/// then the provider chain, caching what the chain answers. One fetch per track
/// at a time: a second request for a track already being fetched joins it.
/// </summary>
/// <remarks>
/// The chain is asked only while <see cref="LookUpOnline"/> is on (a lookup sends the track's title and artist off the machine). Custom and
/// cached lyrics need no lookup, so they are served either way.
/// </remarks>
/// <remarks>
/// A caller's cancellation (skipping to the next track) abandons only that
/// caller's wait. The fetch itself runs on to its provider timeouts and caches
/// its answer, so skipping back to the track finds it ready.
/// </remarks>
public sealed class LyricsService : IDisposable
{
    private readonly CustomLyricsStore? _custom;
    private readonly LyricsCache? _cache;
    private readonly LyricsProviderChain _chain;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, Task<LyricsLookup>> _inFlight = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private volatile bool _lookUpOnline = true;

    /// <summary>Whether the provider chain may be asked. Off, a track with no
    /// custom or cached lyrics comes back <see cref="LyricsStatus.NotLookedUp"/>.</summary>
    public bool LookUpOnline
    {
        get => _lookUpOnline;
        set => _lookUpOnline = value;
    }

    public LyricsService(LyricsProviderChain chain, LyricsCache? cache = null, CustomLyricsStore? custom = null)
    {
        _chain = chain;
        _cache = cache;
        _custom = custom;
    }

    public static LyricsService Create(string dataDirectory, IReadOnlyList<ILyricsProvider> providers) => new(
        new LyricsProviderChain(providers),
        new LyricsCache(Path.Combine(dataDirectory, "lyrics", "cache")),
        new CustomLyricsStore(Path.Combine(dataDirectory, "lyrics", "custom")));

    /// <summary>
    /// Never throws except <see cref="OperationCanceledException"/> for
    /// <paramref name="ct"/>; provider failures come back as
    /// <see cref="LyricsStatus.Unavailable"/>.
    /// </summary>
    public Task<LyricsLookup> GetAsync(LyricsQuery query, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);

        // A request made after lookups were switched on must not join a fetch
        // that started with them off, so the switch is part of the key.
        var online = LookUpOnline;
        var key = online ? query.VideoId : query.VideoId + "|local";
        Task<LyricsLookup> fetch;
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(key, out fetch!))
            {
                fetch = Task.Run(() => FetchAsync(query, online), CancellationToken.None);
                _inFlight[key] = fetch;
                _ = fetch.ContinueWith(
                    _ => { lock (_gate) _inFlight.Remove(key); },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        return fetch.WaitAsync(ct);
    }

    /// <summary>
    /// Looks the track up again without joining a fetch in flight, so a change
    /// the user just made to their own lyrics shows at once rather than the
    /// answer of a fetch that started before it.
    /// </summary>
    public Task<LyricsLookup> ReloadAsync(LyricsQuery query, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);
        var online = LookUpOnline;
        return Task.Run(() => FetchAsync(query, online), CancellationToken.None).WaitAsync(ct);
    }

    public CustomLyrics? GetCustom(string videoId) => _custom?.TryGetRaw(videoId);

    /// <summary>Saves the user's own lyrics for the track. Throws on I/O
    /// failure, for the editor to report; follow with <see cref="ReloadAsync"/>.</summary>
    public void SaveCustom(string videoId, CustomLyrics lyrics) =>
        (_custom ?? throw new InvalidOperationException("No custom lyrics store.")).Save(videoId, lyrics);

    /// <summary>Removes the user's own lyrics for the track, if any. Throws on I/O
    /// failure; follow with <see cref="ReloadAsync"/>.</summary>
    public void RemoveCustom(string videoId) => _custom?.Delete(videoId);

    private async Task<LyricsLookup> FetchAsync(LyricsQuery query, bool online)
    {
        if (_custom?.TryGet(query.VideoId) is { } custom)
            return new LyricsLookup(LyricsStatus.Found, custom);

        var stale = false;
        var cached = _cache?.TryGet(query.VideoId, out stale);
        if (cached is not null && (!stale || !online))
            return cached;

        if (!online)
            return LyricsLookup.NotLookedUp;

        LyricsLookup result;
        try
        {
            result = await _chain.LookupAsync(query, _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            // Only shutdown cancels a fetch; nobody is waiting for a real answer.
            return cached ?? LyricsLookup.Unavailable;
        }

        if (cached is not null && result.Status is not (LyricsStatus.Found or LyricsStatus.Instrumental))
        {
            // A stale entry outlives a failed or empty lookup. Unavailable leaves
            // it stale, to try again; a definite miss keeps the old lyrics for good.
            if (result.Status == LyricsStatus.NotFound)
                _cache?.Put(query.VideoId, cached);
            return cached;
        }

        _cache?.Put(query.VideoId, result);
        return result;
    }

    /// <summary>Cancels fetches in flight. The source is cancelled, not disposed:
    /// a fetch finishing after this still reads its token.</summary>
    public void Dispose() => _shutdown.Cancel();
}
