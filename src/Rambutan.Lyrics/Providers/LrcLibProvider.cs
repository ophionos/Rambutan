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

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public LrcLibProvider(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
    }

    public string Name => SourceName;

    public async Task<LyricsResult?> TryGetAsync(LyricsQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.Title) || string.IsNullOrWhiteSpace(query.Artist))
            return null;

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
                ct);
            if (exact is not null && ToResult(exact) is { } result)
                return result;
        }

        var candidates = await GetAsync(
            "search?" + Query(("track_name", query.Title), ("artist_name", query.Artist)),
            LrcLibJsonContext.Default.LrcLibTrackArray,
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

    /// <summary>GETs and deserializes; null on 404 (LRCLIB's "not found"), throws on other failures.</summary>
    private async Task<T?> GetAsync<T>(string relative, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, relative));
        foreach (var product in UserAgent)
            request.Headers.UserAgent.Add(product);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(body, type, ct);
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
