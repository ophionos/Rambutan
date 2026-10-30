using Rambutan.Lyrics.Providers;

namespace Rambutan.Lyrics.Store;

/// <summary>
/// An online source of lyrics the user can choose: its providers and its own
/// cache, so one source's answer (or miss) is never served for another.
/// </summary>
public sealed record LyricsSource(string Name, LyricsProviderChain Chain, LyricsCache? Cache = null);

/// <summary>
/// Finds lyrics for a track: the user's custom lyrics first, then a local
/// track's own (<see cref="LocalLyrics"/>, never cached), then the chosen
/// source's cache, then that source's chain, caching what the chain answers.
/// One fetch per track and source at a time: a second request for a track
/// already being fetched joins it.
/// </summary>
/// <remarks>
/// Only the chosen <see cref="Source"/> is asked, never the others after it:
/// which source shows is the user's choice. A chain is asked only while
/// <see cref="LookUpOnline"/> is on (a lookup sends the track's details off
/// the machine). Custom, local and cached lyrics need no lookup, so they are
/// served either way.
/// </remarks>
/// <remarks>
/// A caller's cancellation (skipping to the next track) abandons only that
/// caller's wait. The fetch itself runs on to its provider timeouts and caches
/// its answer, so skipping back to the track finds it ready.
/// </remarks>
public sealed class LyricsService : IDisposable
{
    private readonly CustomLyricsStore? _custom;
    private readonly LocalLyrics? _local;
    private readonly IReadOnlyList<LyricsSource> _sources;
    private volatile LyricsSource _source;
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

    /// <summary>The sources' names, the first being the one chosen until
    /// <see cref="Source"/> says otherwise.</summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>The source asked for lyrics. A name that isn't one of
    /// <see cref="Sources"/> (a newer build's choice) chooses the first.</summary>
    public string Source
    {
        get => _source.Name;
        set => _source = _sources.FirstOrDefault(s => s.Name == value) ?? _sources[0];
    }

    public LyricsService(
        LyricsProviderChain chain, LyricsCache? cache = null, CustomLyricsStore? custom = null, LocalLyrics? local = null)
        : this([new LyricsSource("default", chain, cache)], custom, local)
    {
    }

    /// <param name="sources">At least one; the first is chosen to begin with.</param>
    public LyricsService(IReadOnlyList<LyricsSource> sources, CustomLyricsStore? custom = null, LocalLyrics? local = null)
    {
        if (sources.Count == 0)
            throw new ArgumentException("A lyrics service needs a source.", nameof(sources));
        _sources = sources;
        _source = sources[0];
        Sources = sources.Select(s => s.Name).ToArray();
        _custom = custom;
        _local = local;
    }

    /// <summary>
    /// One source per provider, in order. The first keeps its cache in
    /// <c>lyrics/cache</c>, where the cache was before there was a choice of
    /// source, so what it found then is still found; each other source's is a
    /// folder of its name under it.
    /// </summary>
    public static LyricsService Create(
        string dataDirectory, IReadOnlyList<ILyricsProvider> providers, LocalLyrics? local = null)
    {
        var cache = Path.Combine(dataDirectory, "lyrics", "cache");
        LyricsSource[] sources = providers.Count == 0
            ? [new LyricsSource("none", new LyricsProviderChain([]), new LyricsCache(cache))]
            : providers.Select((p, i) => new LyricsSource(
                p.Name,
                new LyricsProviderChain([p]),
                new LyricsCache(i == 0 ? cache : Path.Combine(cache, p.Name)))).ToArray();
        return new LyricsService(sources, new CustomLyricsStore(Path.Combine(dataDirectory, "lyrics", "custom")), local);
    }

    /// <summary>
    /// Never throws except <see cref="OperationCanceledException"/> for
    /// <paramref name="ct"/>; provider failures come back as
    /// <see cref="LyricsStatus.Unavailable"/>.
    /// </summary>
    public Task<LyricsLookup> GetAsync(LyricsQuery query, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);

        // A request made after lookups were switched on, or for another source,
        // must not join a fetch that started before, so both are in the key.
        var online = LookUpOnline;
        var source = _source;
        var key = source.Name + "|" + query.VideoId + (online ? "" : "|local");
        Task<LyricsLookup> fetch;
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(key, out fetch!))
            {
                fetch = Task.Run(() => FetchAsync(query, online, source), CancellationToken.None);
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
        var source = _source;
        return Task.Run(() => FetchAsync(query, online, source), CancellationToken.None).WaitAsync(ct);
    }

    public CustomLyrics? GetCustom(string videoId) => _custom?.TryGetRaw(videoId);

    /// <summary>Saves the user's own lyrics for the track. Throws on I/O
    /// failure, for the editor to report; follow with <see cref="ReloadAsync"/>.</summary>
    public void SaveCustom(string videoId, CustomLyrics lyrics) =>
        (_custom ?? throw new InvalidOperationException("No custom lyrics store.")).Save(videoId, lyrics);

    /// <summary>Removes the user's own lyrics for the track, if any. Throws on I/O
    /// failure; follow with <see cref="ReloadAsync"/>.</summary>
    public void RemoveCustom(string videoId) => _custom?.Delete(videoId);

    private async Task<LyricsLookup> FetchAsync(LyricsQuery query, bool online, LyricsSource source)
    {
        if (_custom?.TryGet(query.VideoId) is { } custom)
            return new LyricsLookup(LyricsStatus.Found, custom);

        // Before the cache: lyrics LRCLIB gave the track earlier mustn't hide an
        // .lrc file the user put beside it since.
        if (_local is not null)
        {
            try
            {
                if (await _local.TryGetAsync(query.VideoId, _shutdown.Token) is { } own)
                    return new LyricsLookup(LyricsStatus.Found, own);
            }
            catch (OperationCanceledException)
            {
                return LyricsLookup.Unavailable;
            }
        }

        var stale = false;
        var cache = source.Cache;
        var cached = cache?.TryGet(query.VideoId, out stale);
        if (cached is not null && (!stale || !online))
            return cached;

        if (!online)
            return LyricsLookup.NotLookedUp;

        LyricsLookup result;
        try
        {
            result = await source.Chain.LookupAsync(query, _shutdown.Token);
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
                cache?.Put(query.VideoId, cached);
            return cached;
        }

        cache?.Put(query.VideoId, result);
        return result;
    }

    /// <summary>Cancels fetches in flight. The source is cancelled, not disposed:
    /// a fetch finishing after this still reads its token.</summary>
    public void Dispose() => _shutdown.Cancel();
}
