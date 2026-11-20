using System.Net;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Rambutan.Core.Stations;

namespace Rambutan.Integrations.ListenBrainz;

/// <summary>
/// ListenBrainz's LB Radio as an artist's station. The prompt API exists only on
/// ListenBrainz itself, so the server is fixed and the user's token never goes to
/// another one. It needs that token even to read.
///
/// An <c>artist:(name)</c> prompt fails for a name MusicBrainz finds ambiguous, so
/// the artist is first looked up by some of its song titles to get its MusicBrainz
/// id, which a prompt takes without ambiguity. It translates, it doesn't decide:
/// a refusal is <see cref="StationTokenRejectedException"/>, any other answer
/// without a station <see cref="StationUnavailableException"/>.
///
/// It also reads the playlists ListenBrainz makes for a user. Those are public, so
/// their requests carry no token, even when one is set.
/// </summary>
public sealed class ListenBrainzRadioClient : IArtistStationSource, ICreatedForSource
{
    public static readonly Uri BaseUri = new("https://api.listenbrainz.org/");

    private const int MaxLookups = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<string?> _token;
    private readonly HttpClient _http;

    public ListenBrainzRadioClient(Func<string?> token, HttpClient? http = null)
    {
        _token = token;
        _http = http ?? new HttpClient(
            new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });
        var version = typeof(ListenBrainzRadioClient).Assembly.GetName().Version;
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Rambutan", version is null ? "dev" : version.ToString(3)));
    }

    public static Uri RadioUri(string prompt) =>
        new(BaseUri, $"1/explore/lb-radio?prompt={Uri.EscapeDataString(prompt)}&mode=easy");

    public static Uri LookupUri(string artist, string title) =>
        new(BaseUri, $"1/metadata/lookup/?artist_name={Uri.EscapeDataString(artist)}&recording_name={Uri.EscapeDataString(title)}");

    public static Uri CreatedForUri(string user) =>
        new(BaseUri, $"1/user/{Uri.EscapeDataString(user)}/playlists/createdfor?count=100");

    public static Uri PlaylistUri(string id) =>
        new(BaseUri, $"1/playlist/{Uri.EscapeDataString(id)}");

    public async Task<IReadOnlyList<CreatedForPlaylist>> GetCreatedForAsync(string user, CancellationToken ct = default) =>
        ParseCreatedFor(await FetchCreatedForAsync(user, ct));

    public async Task<IReadOnlyList<StationTrack>> GetPlaylistAsync(string id, CancellationToken ct = default) =>
        ParsePlaylist(await FetchPlaylistAsync(id, ct));

    /// <summary>The raw list of playlists made for the user, for the fixture recorder.</summary>
    public Task<string> FetchCreatedForAsync(string user, CancellationToken ct = default) =>
        GetAsync(CreatedForUri(user), null, ct);

    /// <summary>A playlist's raw answer, for the fixture recorder.</summary>
    public Task<string> FetchPlaylistAsync(string id, CancellationToken ct = default) =>
        GetAsync(PlaylistUri(id), null, ct);

    public async Task<IReadOnlyList<StationTrack>> GetArtistStationAsync(ArtistSeed artist, CancellationToken ct = default) =>
        ParseRadio(await FetchRadioAsync(artist, ct));

    /// <summary>The radio's raw answer for the artist, for the fixture recorder.</summary>
    public async Task<string> FetchRadioAsync(ArtistSeed artist, CancellationToken ct = default)
    {
        var token = _token();
        if (string.IsNullOrEmpty(token))
            throw new StationTokenRejectedException("There is no ListenBrainz token.");

        var mbid = await FindArtistAsync(artist, token, ct);
        if (mbid is null && artist.Name.AsSpan().IndexOfAny('(', ')') >= 0)
            throw new StationUnavailableException("ListenBrainz can't be asked for an artist whose name has parentheses.");

        return await GetAsync(RadioUri($"artist:({mbid ?? artist.Name})"), token, ct);
    }

    private async Task<string?> FindArtistAsync(ArtistSeed artist, string token, CancellationToken ct)
    {
        foreach (var title in artist.SongTitles
                     .Select(t => t?.Trim())
                     .Where(t => !string.IsNullOrEmpty(t))
                     .Distinct(StringComparer.Ordinal)
                     .Take(MaxLookups))
        {
            var body = await GetAsync(LookupUri(artist.Name, title!), token, ct);
            if (ParseLookup(body, artist.Name) is { } mbid)
                return mbid;
        }
        return null;
    }

    private async Task<string> GetAsync(Uri uri, string? token, CancellationToken ct)
    {
        string body;
        HttpStatusCode status;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(RequestTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (token is not null)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
                using var response = await _http.SendAsync(request, timeout.Token);
                status = response.StatusCode;
                body = await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new HttpRequestException("ListenBrainz didn't answer in time.", ex);
            }
        }

        var code = (int)status;
        if (code is >= 200 and < 300)
            return body;
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new StationTokenRejectedException($"ListenBrainz refused the token (HTTP {code}).");
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            throw new StationUnavailableException(ErrorText(body) ?? $"ListenBrainz answered HTTP {code} without a station.");
        throw new HttpRequestException($"ListenBrainz answered HTTP {code}.", null, status);
    }

    private static string? ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("error", out var e) &&
                   e.ValueKind == JsonValueKind.String &&
                   e.GetString() is { Length: > 0 } text
                ? text
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The tracks of a radio answer, in order. A track without a title or artist
    /// credit is skipped; one without a duration keeps it null. The answer has no
    /// YouTube link, so none is set.
    /// </summary>
    public static IReadOnlyList<StationTrack> ParseRadio(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new StationUnavailableException("ListenBrainz answered without a station.", ex);
        }

        using (doc)
        {
            if (!TryGetObject(doc.RootElement, "payload", out var payload) ||
                !TryGetObject(payload, "jspf", out var jspf) ||
                !TryGetObject(jspf, "playlist", out var playlist) ||
                !playlist.TryGetProperty("track", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new StationUnavailableException("ListenBrainz answered without a playlist.");

            return ReadTracks(items);
        }
    }

    /// <summary>The tracks of a playlist answer, in order, by the radio's rules.</summary>
    public static IReadOnlyList<StationTrack> ParsePlaylist(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new StationUnavailableException("ListenBrainz answered without a playlist.", ex);
        }

        using (doc)
        {
            if (!TryGetObject(doc.RootElement, "playlist", out var playlist) ||
                !playlist.TryGetProperty("track", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new StationUnavailableException("ListenBrainz answered without a playlist.");
            return ReadTracks(items);
        }
    }

    /// <summary>
    /// The playlists of the known kinds in a list answer. Its <c>expires_at</c>
    /// sometimes has no offset; that is UTC. A playlist without a readable date or
    /// of another kind (the yearly ones) is skipped.
    /// </summary>
    public static IReadOnlyList<CreatedForPlaylist> ParseCreatedFor(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new StationUnavailableException("ListenBrainz answered without a list of playlists.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("playlists", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new StationUnavailableException("ListenBrainz answered without a list of playlists.");

            var result = new List<CreatedForPlaylist>();
            foreach (var item in items.EnumerateArray())
            {
                if (!TryGetObject(item, "playlist", out var playlist) ||
                    String(playlist, "identifier") is not { } identifier ||
                    identifier.TrimEnd('/').Split('/')[^1] is not { Length: > 0 } id ||
                    ParseDate(String(playlist, "date")) is not { } created ||
                    !TryGetObject(playlist, "extension", out var extension) ||
                    !TryGetObject(extension, "https://musicbrainz.org/doc/jspf#playlist", out var jspf) ||
                    !TryGetObject(jspf, "additional_metadata", out var meta))
                    continue;

                var source = TryGetObject(meta, "algorithm_metadata", out var algorithm)
                    ? String(algorithm, "source_patch")
                    : null;
                CreatedForKind? kind = source switch
                {
                    "daily-jams" => CreatedForKind.DailyJams,
                    "weekly-jams" => CreatedForKind.WeeklyJams,
                    "weekly-exploration" => CreatedForKind.WeeklyExploration,
                    _ => null,
                };
                if (kind is null)
                    continue;

                result.Add(new CreatedForPlaylist(kind.Value, id, created, ParseDate(String(meta, "expires_at"))));
            }
            return result;
        }
    }

    private static DateTimeOffset? ParseDate(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;

    private static List<StationTrack> ReadTracks(JsonElement items)
    {
        var tracks = new List<StationTrack>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                String(item, "title") is not { } title ||
                String(item, "creator") is not { } creator)
                continue;

            TimeSpan? duration = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number &&
                                 d.TryGetInt64(out var ms) && ms > 0
                ? TimeSpan.FromMilliseconds(ms)
                : null;
            tracks.Add(new StationTrack(creator, title, duration));
        }
        return tracks;
    }

    /// <summary>
    /// The artist's MusicBrainz id from a metadata lookup, or null when the lookup
    /// missed (an empty object) or found a recording credited to someone else.
    /// </summary>
    public static string? ParseLookup(string body, string artist)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                String(root, "artist_credit_name") is not { } credit ||
                !StationArtist.Names(credit, artist) ||
                !root.TryGetProperty("artist_mbids", out var mbids) || mbids.ValueKind != JsonValueKind.Array)
                return null;
            return mbids.EnumerateArray()
                .Select(m => m.ValueKind == JsonValueKind.String ? m.GetString()?.Trim() : null)
                .FirstOrDefault(m => !string.IsNullOrEmpty(m));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetObject(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) &&
               value.ValueKind == JsonValueKind.Object;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString()?.Trim() is { Length: > 0 } text
            ? text
            : null;
}
