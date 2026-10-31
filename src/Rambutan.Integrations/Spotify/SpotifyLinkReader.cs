using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Import;

namespace Rambutan.Integrations.Spotify;

/// <summary>The list a link names isn't public on Spotify: private, removed, or
/// never there. Spotify answers such an embed page with its "Page not found".</summary>
public sealed class SpotifyListUnavailableException(string message) : Exception(message);

/// <summary>The web player's API refused a request, or answered something
/// that isn't what was asked.</summary>
public sealed class SpotifyApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What an embed page holds: the list's first songs, and the
/// anonymous key the page's player was given.</summary>
public sealed record SpotifyEmbed(ImportedList List, string? AccessToken);

/// <summary>A page of a playlist's songs from the web player's API.</summary>
/// <param name="Total">How many rows the playlist has; null when the answer didn't say.</param>
public sealed record SpotifyPlaylistPage(IReadOnlyList<ImportedTrack> Tracks, int Skipped, int? Total);

/// <summary>
/// Reads a public playlist or album from its share link, with no Spotify
/// account and without Spotify's Web API (which limits an app in development
/// mode to five users). The embed page (<c>open.spotify.com/embed/…</c>)
/// lists the first <see cref="EmbedLimit"/> songs and carries an anonymous key,
/// which the web player's API (<c>pathfinder</c>) accepts for the rest of a
/// playlist. Both are undocumented and change: when the rest can't be read,
/// the list keeps its first songs and is marked
/// <see cref="ImportedList.Incomplete"/>.
///
/// It translates, it doesn't decide: what to do with an incomplete list is
/// the import's.
/// </summary>
public sealed class SpotifyLinkReader
{
    /// <summary>The most songs an embed page lists.</summary>
    public const int EmbedLimit = 100;

    /// <summary>The songs asked for in one request for the rest.</summary>
    public const int PageSize = 100;

    public static readonly Uri PathfinderUri = new("https://api-partner.spotify.com/pathfinder/v2/query");

    /// <summary>
    /// The web player's persisted query for a playlist's rows. Spotify names
    /// queries by their hash, which changes with the web player's builds; the
    /// fixture recorder's <c>spotify-live-diff</c> says when this one is no
    /// longer taken.
    /// </summary>
    public const string PlaylistContentsHash = "21ea11a6ac75a5f3e01fd365d29c18e0a4a8b7dc09f89bbb5d73910190c6acaf";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The embed page puts this between a song's artists; a plain
    /// comma can be part of a name ("Tyler, The Creator").</summary>
    private const string ArtistSeparator = ", ";

    private readonly HttpClient _http;

    public SpotifyLinkReader(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        var version = typeof(SpotifyLinkReader).Assembly.GetName().Version;
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Rambutan", version is null ? "dev" : version.ToString(3)));
    }

    /// <summary>
    /// The list a link names, in Spotify's order. A list the embed page shows
    /// in full is read with that one request; a longer playlist asks the web
    /// player's API for the rest, a page at a time.
    /// </summary>
    /// <exception cref="SpotifyListUnavailableException">The list isn't public.</exception>
    /// <exception cref="HttpRequestException">The embed page couldn't be read.</exception>
    public async Task<ImportedList> ReadAsync(SpotifyListLink link, CancellationToken ct = default)
    {
        var (body, status) = await SendAsync(new HttpRequestMessage(HttpMethod.Get, link.EmbedUrl), ct);
        var embed = ParseEmbed(body, status, link);
        var list = embed.List;
        // Fewer rows than the page's limit is the whole list; at the limit,
        // the page doesn't say whether there are more.
        if (list.Tracks.Count + list.Skipped < EmbedLimit)
            return list;
        if (link.Kind != SpotifyListKind.Playlist || embed.AccessToken is not { Length: > 0 } token)
            return list with { Incomplete = true };

        var tracks = list.Tracks.ToList();
        var skipped = list.Skipped;
        var offset = EmbedLimit;
        try
        {
            while (true)
            {
                var page = await ReadPageAsync(link, token, offset, ct);
                tracks.AddRange(page.Tracks);
                skipped += page.Skipped;
                var rows = page.Tracks.Count + page.Skipped;
                offset += rows;
                if (rows == 0 || (page.Total is { } total ? offset >= total : rows < PageSize))
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or SpotifyApiException)
        {
            AppLog.Write("spotify", $"Read the first {offset} rows of {link.Uri}; the rest failed: {ex.Message}");
            return list with { Tracks = tracks, Skipped = skipped, Incomplete = true };
        }
        return list with { Tracks = tracks, Skipped = skipped };
    }

    /// <summary>A page of a playlist's rows, from <paramref name="offset"/>.</summary>
    /// <exception cref="SpotifyApiException">The answer isn't a page of the playlist.</exception>
    /// <exception cref="HttpRequestException">The request failed.</exception>
    public async Task<SpotifyPlaylistPage> ReadPageAsync(
        SpotifyListLink link, string accessToken, int offset, CancellationToken ct = default)
    {
        var (body, status) = await SendAsync(PageRequest(link, accessToken, offset), ct);
        return ParsePlaylistPage(body, status);
    }

    /// <summary>The request for a page of a playlist's rows, as the web player sends it.</summary>
    public static HttpRequestMessage PageRequest(SpotifyListLink link, string accessToken, int offset)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, PathfinderUri)
        {
            Content = new StringContent(PageQuery(link, offset, PageSize), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("app-platform", "WebPlayer");
        return request;
    }

    /// <summary>The request body for a page of a playlist's rows.</summary>
    public static string PageQuery(SpotifyListLink link, int offset, int limit)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartObject("variables");
            json.WriteString("uri", link.Uri);
            json.WriteNumber("offset", offset);
            json.WriteNumber("limit", limit);
            json.WriteEndObject();
            json.WriteString("operationName", "fetchPlaylistContents");
            json.WriteStartObject("extensions");
            json.WriteStartObject("persistedQuery");
            json.WriteNumber("version", 1);
            json.WriteString("sha256Hash", PlaylistContentsHash);
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Sends a request and reads the whole answer, whatever its status.</summary>
    /// <exception cref="HttpRequestException">No answer, or none in time.</exception>
    public async Task<(string Body, int Status)> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        using (request)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(RequestTimeout);
            try
            {
                using var response = await _http.SendAsync(request, timeout.Token);
                return (await response.Content.ReadAsStringAsync(timeout.Token), (int)response.StatusCode);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new HttpRequestException("Spotify didn't answer in time.", ex);
            }
        }
    }

    /// <summary>
    /// The list on an embed page: its name, and its songs in order. A row
    /// that isn't a song (a podcast episode) or has no title is counted as
    /// skipped. An album's songs take its name as their album.
    /// </summary>
    /// <exception cref="SpotifyListUnavailableException">Spotify's "Page not found".</exception>
    /// <exception cref="HttpRequestException">Not an embed page.</exception>
    public static SpotifyEmbed ParseEmbed(string html, int status, SpotifyListLink link)
    {
        if (NextData(html) is not { } json)
            throw new HttpRequestException($"Spotify answered HTTP {status} without its embed page.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException($"Spotify's embed page (HTTP {status}) couldn't be read.", ex);
        }

        using (doc)
        {
            var props = doc.RootElement.Get("props").Get("pageProps");
            var entity = props.Get("state").Get("data").Get("entity");
            if (entity.ValueKind != JsonValueKind.Object)
            {
                if (status == 404 || props.Get("status") is { ValueKind: JsonValueKind.Number } s && s.GetInt32() == 404)
                    throw new SpotifyListUnavailableException($"Spotify has no public list at {link.WebUrl}.");
                throw new HttpRequestException($"Spotify's embed page (HTTP {status}) has no list.");
            }

            var name = entity.String("name") ?? entity.String("title") ?? "";
            var album = link.Kind == SpotifyListKind.Album ? name : null;
            var tracks = new List<ImportedTrack>();
            var skipped = 0;
            if (entity.Get("trackList") is { ValueKind: JsonValueKind.Array } rows)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.String("entityType") is { } type && type != "track" ||
                        row.String("title") is not { } title)
                    {
                        skipped++;
                        continue;
                    }
                    string[] artists = row.String("subtitle") is { } subtitle
                        ? subtitle.Split(ArtistSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        : [];
                    tracks.Add(new ImportedTrack(title, artists, album, Duration(row.Get("duration")),
                        SpotifyIds.TrackId(row.String("uri"))));
                }
            }

            var token = props.Get("state").Get("settings").Get("session").String("accessToken");
            return new SpotifyEmbed(
                new ImportedList(ImportedListKind.Playlist, name, tracks, skipped, link.Uri), token);
        }
    }

    /// <summary>A page of a playlist's rows from the web player's API. A row
    /// that isn't a song, or has no title, is counted as skipped.</summary>
    /// <exception cref="SpotifyApiException">The answer isn't a page of a playlist.</exception>
    public static SpotifyPlaylistPage ParsePlaylistPage(string body, int status = 200)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new SpotifyApiException($"Spotify's API answered HTTP {status} without JSON.", ex);
        }

        using (doc)
        {
            var content = doc.RootElement.Get("data").Get("playlistV2").Get("content");
            if (content.Get("items") is not { ValueKind: JsonValueKind.Array } items)
            {
                var error = doc.RootElement.Get("errors") is { ValueKind: JsonValueKind.Array } errors
                    ? errors.EnumerateArray().Select(e => e.String("message")).FirstOrDefault(m => m is not null)
                    : null;
                throw new SpotifyApiException($"Spotify's API answered HTTP {status} without the playlist's songs" +
                                              (error is null ? "." : $": {error}"));
            }

            var tracks = new List<ImportedTrack>();
            var skipped = 0;
            foreach (var item in items.EnumerateArray())
            {
                var track = item.Get("itemV2").Get("data");
                if (track.String("__typename") != "Track" || track.String("name") is not { } title)
                {
                    skipped++;
                    continue;
                }
                List<string> artists = track.Get("artists").Get("items") is { ValueKind: JsonValueKind.Array } list
                    ? list.EnumerateArray().Select(a => a.Get("profile").String("name")).OfType<string>().ToList()
                    : [];
                tracks.Add(new ImportedTrack(title, artists, track.Get("albumOfTrack").String("name"),
                    Duration(track.Get("trackDuration").Get("totalMilliseconds")), SpotifyIds.TrackId(track.String("uri"))));
            }

            int? total = content.Get("totalCount") is { ValueKind: JsonValueKind.Number } count && count.TryGetInt32(out var n)
                ? n
                : null;
            return new SpotifyPlaylistPage(tracks, skipped, total);
        }
    }

    // The JSON a Next.js page carries for its scripts.
    private static string? NextData(string html)
    {
        const string open = "<script id=\"__NEXT_DATA__\" type=\"application/json\">";
        var start = html.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += open.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        return end < 0 ? null : html[start..end];
    }

    private static TimeSpan? Duration(JsonElement ms) =>
        ms.ValueKind == JsonValueKind.Number && ms.TryGetInt64(out var value) && value > 0
            ? TimeSpan.FromMilliseconds(value)
            : null;
}

internal static class SpotifyJson
{
    /// <summary>The named property of an object; an undefined element otherwise.</summary>
    public static JsonElement Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    /// <summary>The named property's text, trimmed; null when it is missing, not text or empty.</summary>
    public static string? String(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.String } value && value.GetString()?.Trim() is { Length: > 0 } text
            ? text
            : null;
}
