using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Parsing;

namespace Rambutan.Lyrics.Providers;

/// <summary>
/// LRCLIB (lrclib.net), a free, keyless lyrics database: line-level LRC for
/// most tracks, word-level for the few with a word-synced Lyricsfile. Asks
/// <c>/api/get</c> for an exact match first, then falls back to
/// <c>/api/search</c> and keeps only a result whose length is within
/// <see cref="DurationTolerance"/> of the track's, since lyrics timed for a radio
/// edit drift against the album cut.
/// </summary>
public sealed class LrcLibProvider : ILyricsProvider, IDisposable
{
    public const string SourceName = "lrclib";

    public static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(3);

    private static readonly Uri BaseUri = new("https://lrclib.net/api/");

    // LRCLIB asks clients to identify themselves with a name, version and link.
    private static readonly ProductInfoHeaderValue[] UserAgent =
    [
        new("Rambutan", AppVersion()),
        new("(+https://github.com/ophionos/Rambutan)"),
    ];

    /// <summary>The waits before the retries of a request that failed transiently,
    /// when LRCLIB sends no <c>Retry-After</c>. LRCLIB is a free service, so few.</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)];

    /// <summary>A retry starts at most this long after the lookup began, so the
    /// request it makes still has time within the chain's
    /// <see cref="LyricsProviderChain.DefaultTimeout"/>.</summary>
    public static readonly TimeSpan RetryWithin = TimeSpan.FromSeconds(2.5);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeProvider _time;

    public LrcLibProvider(HttpClient? http = null, TimeProvider? time = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _time = time ?? TimeProvider.System;
    }

    public string Name => SourceName;

    public async Task<LyricsResult?> TryGetAsync(LyricsQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.Title) || string.IsNullOrWhiteSpace(query.Artist))
            return null;

        var started = _time.GetTimestamp();

        // /api/get needs all four fields; without them it can only 400.
        if (query.Album is not null && query.Duration is { } duration)
        {
            var exact = await GetAsync(
                "get?" + Query(
                    ("track_name", query.Title),
                    ("artist_name", query.Artist),
                    ("album_name", query.Album),
                    ("duration", ((int)Math.Round(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture))),
                LrcLibJsonContext.Default.LrcLibTrack,
                started,
                ct);
            if (exact is not null && ToResult(exact) is { } result)
                return result;
        }

        var candidates = await GetAsync(
            "search?" + Query(("track_name", query.Title), ("artist_name", query.Artist)),
            LrcLibJsonContext.Default.LrcLibTrackArray,
            started,
            ct);
        return candidates is null ? null : PickFromSearch(candidates, query);
    }

    /// <summary>
    /// The best usable search result: one that matches the track's length (or,
    /// when the length is unknown, its title and artist exactly), preferring
    /// word-synced lyrics over line-synced over plain over an instrumental flag,
    /// then the closest length.
    /// </summary>
    private static LyricsResult? PickFromSearch(IEnumerable<LrcLibTrack?> candidates, LyricsQuery query)
    {
        return candidates
            .OfType<LrcLibTrack>()
            .Where(c => Matches(c, query))
            .Select(c => (Track: c, Result: ToResult(c)))
            .Where(c => c.Result is not null)
            .OrderBy(c => Rank(c.Result!))
            .ThenBy(c => query.Duration is { } d && c.Track.Duration is { } s
                ? Math.Abs(s - d.TotalSeconds)
                : 0)
            .Select(c => c.Result)
            .FirstOrDefault();

        static int Rank(LyricsResult r) =>
            r.IsInstrumental ? 3
            : r.Document.Lines.Any(l => l.Words is { Count: > 0 }) ? 0
            : r.Document.IsSynced ? 1
            : 2;
    }

    private static bool Matches(LrcLibTrack track, LyricsQuery query)
    {
        if (query.Duration is { } duration)
        {
            return track.Duration is { } seconds &&
                Math.Abs(seconds - duration.TotalSeconds) <= DurationTolerance.TotalSeconds;
        }

        return string.Equals(track.TrackName?.Trim(), query.Title.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(track.ArtistName?.Trim(), query.Artist.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Word-synced lyrics when the record has them and its Lyricsfile reads, else
    /// synced lyrics when present and actually stamped, else plain, else the
    /// instrumental flag; null when the record carries nothing usable.
    /// </summary>
    private static LyricsResult? ToResult(LrcLibTrack track)
    {
        // Only for word sync: a line-level Lyricsfile says what syncedLyrics says.
        if (track.HasWordSync == true && !string.IsNullOrWhiteSpace(track.Lyricsfile) &&
            LyricsfileParser.Parse(track.Lyricsfile, SourceName) is { } words &&
            words.Lines.Any(l => l.Words is { Count: > 0 }))
        {
            return LyricsResult.Found(words);
        }

        if (!string.IsNullOrWhiteSpace(track.SyncedLyrics))
        {
            var synced = LrcParser.Parse(track.SyncedLyrics, SourceName);
            if (synced.IsSynced && synced.Lines.Count > 0)
                return LyricsResult.Found(synced);
        }

        if (!string.IsNullOrWhiteSpace(track.PlainLyrics))
        {
            var plain = LyricsDocument.FromPlainText(track.PlainLyrics, SourceName);
            if (plain.Lines.Count > 0)
                return LyricsResult.Found(plain);
        }

        return track.Instrumental ? LyricsResult.Instrumental(SourceName) : null;
    }

    /// <summary>
    /// GETs and deserializes; null on 404 (LRCLIB's "not found"), throws on other
    /// failures. A transient one (LRCLIB answers 503 often, at random through the
    /// day) is retried after <see cref="RetryDelays"/>, or LRCLIB's
    /// <c>Retry-After</c>, while the retry would start within
    /// <see cref="RetryWithin"/> of <paramref name="started"/>; otherwise it throws at once.
    /// </summary>
    private async Task<T?> GetAsync<T>(string relative, JsonTypeInfo<T> type, long started, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, relative));
            foreach (var product in UserAgent)
                request.Headers.UserAgent.Add(product);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException) when (RetryDelay(attempt, null, started) is { } wait)
            {
                await Task.Delay(wait, _time, ct);
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return default;
                if (IsTransient(response.StatusCode) && RetryDelay(attempt, response.Headers.RetryAfter, started) is { } wait)
                {
                    await Task.Delay(wait, _time, ct);
                    continue;
                }
                response.EnsureSuccessStatusCode();

                await using var body = await response.Content.ReadAsStreamAsync(ct);
                return await JsonSerializer.DeserializeAsync(body, type, ct);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) => status is
        HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>How long to wait before retrying after failed attempt
    /// <paramref name="attempt"/>; null when no retry is left or it would start too late.</summary>
    private TimeSpan? RetryDelay(int attempt, RetryConditionHeaderValue? retryAfter, long started)
    {
        if (attempt >= RetryDelays.Length)
            return null;
        var wait = retryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - _time.GetUtcNow(),
            _ => RetryDelays[attempt],
        };
        if (wait < TimeSpan.Zero)
            wait = TimeSpan.Zero;
        return _time.GetElapsedTime(started) + wait <= RetryWithin ? wait : null;
    }

    private static string Query(params (string Key, string Value)[] pairs) =>
        string.Join('&', pairs.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));

    private static string AppVersion()
    {
        var version = typeof(LrcLibProvider).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0];
        return string.IsNullOrEmpty(version) ? "0" : version;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}

/// <summary>One LRCLIB record, as <c>/api/get</c> returns it and <c>/api/search</c> lists it.</summary>
internal sealed record LrcLibTrack(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("artistName")] string? ArtistName,
    [property: JsonPropertyName("albumName")] string? AlbumName,
    [property: JsonPropertyName("duration")] double? Duration,
    [property: JsonPropertyName("instrumental")] bool Instrumental,
    [property: JsonPropertyName("plainLyrics")] string? PlainLyrics,
    [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics,
    [property: JsonPropertyName("hasWordSync")] bool? HasWordSync = null,
    [property: JsonPropertyName("lyricsfile")] string? Lyricsfile = null);

[JsonSerializable(typeof(LrcLibTrack))]
[JsonSerializable(typeof(LrcLibTrack[]))]
internal sealed partial class LrcLibJsonContext : JsonSerializerContext;
