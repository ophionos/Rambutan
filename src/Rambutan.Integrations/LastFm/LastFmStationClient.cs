using System.Net.Http.Headers;
using System.Text.Json;
using Rambutan.Core.Stations;

namespace Rambutan.Integrations.LastFm;

/// <summary>
/// Last.fm's stations (a user's mix, recommendations and library, and an
/// artist's similar-artists station) as its web player loads them. The API has
/// no such method, so this reads the player's JSON endpoint, which is
/// undocumented and needs no key or sign-in. Each request returns
/// a fresh sample of the station.
///
/// It translates, it doesn't decide: a response that isn't a station (an
/// unknown user answers HTTP 500 with an HTML page, an unknown artist HTTP 404)
/// is <see cref="StationUnavailableException"/>, whatever its status code.
/// </summary>
public sealed class LastFmStationClient : IStationSource
{
    public static readonly Uri BaseUri = new("https://www.last.fm/player/station/user/");

    /// <summary>Artists' stations. <c>music/&lt;artist&gt;</c> alone is only the
    /// artist's top tracks, the same list on every request; <c>+similar</c> is
    /// the station.</summary>
    public static readonly Uri ArtistBaseUri = new("https://www.last.fm/player/station/music/");

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;

    public LastFmStationClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        var version = typeof(LastFmStationClient).Assembly.GetName().Version;
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Rambutan", version is null ? "dev" : version.ToString(3)));
    }

    public static Uri StationUri(string user, StationKind kind) => new(BaseUri, $"{Uri.EscapeDataString(user)}/{kind switch
    {
        StationKind.Mix => "mix",
        StationKind.Recommended => "recommended",
        StationKind.Library => "library",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    }}");

    public static Uri ArtistStationUri(string artist) =>
        new(ArtistBaseUri, $"{Uri.EscapeDataString(artist)}/+similar");

    public Task<IReadOnlyList<StationTrack>> GetStationAsync(
        string user, StationKind kind, CancellationToken ct = default) =>
        FetchAsync(StationUri(user, kind), ct);

    public Task<IReadOnlyList<StationTrack>> GetArtistStationAsync(string artist, CancellationToken ct = default) =>
        FetchAsync(ArtistStationUri(artist), ct);

    private async Task<IReadOnlyList<StationTrack>> FetchAsync(Uri uri, CancellationToken ct)
    {
        string body;
        int status;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(RequestTimeout);
            try
            {
                using var response = await _http.GetAsync(uri, timeout.Token);
                status = (int)response.StatusCode;
                body = await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new HttpRequestException("Last.fm didn't answer in time.", ex);
            }
        }
        return Parse(body, status);
    }

    /// <summary>
    /// The tracks in a station response, in order. A track without a title or
    /// artist is skipped; one without a duration or a YouTube link keeps them null.
    /// </summary>
    public static IReadOnlyList<StationTrack> Parse(string body, int status = 200)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new StationUnavailableException($"Last.fm answered HTTP {status} without a station.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("playlist", out var playlist) ||
                playlist.ValueKind != JsonValueKind.Array)
                throw new StationUnavailableException($"Last.fm answered HTTP {status} without a playlist.");

            var tracks = new List<StationTrack>(playlist.GetArrayLength());
            foreach (var item in playlist.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    String(item, "name") is not { } title ||
                    !item.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array ||
                    artists.EnumerateArray().Select(a => String(a, "name")).FirstOrDefault(n => n is not null) is not { } artist)
                    continue;

                TimeSpan? duration = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var seconds) && seconds > 0
                    ? TimeSpan.FromSeconds(seconds)
                    : null;
                tracks.Add(new StationTrack(artist, title, duration, YouTubeId(item)));
            }
            return tracks;
        }
    }

    private static string? YouTubeId(JsonElement track)
    {
        foreach (var name in (ReadOnlySpan<string>)["playlinks", "_playlinks"])
        {
            if (!track.TryGetProperty(name, out var links) || links.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var link in links.EnumerateArray())
                if (String(link, "affiliate") == "youtube" && String(link, "id") is { } id)
                    return id;
        }
        return null;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;
}
