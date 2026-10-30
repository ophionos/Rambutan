using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.App.Services.Demo;

/// <summary>
/// Answers InnerTube requests with the recorded fixtures, so the real client and
/// parsers run end to end. The recordings are a fixed sample: every album opens the
/// same album, every artist the same artist, and every search returns the recorded
/// "daft punk" results for the chosen filter (or the "All" overview, unfiltered). A query starting with "empty" returns
/// no results, to show that state. Requests with no recording get a 404, which the
/// app then shows as an error, the same as an unexpected real response would.
/// </summary>
public sealed class DemoInnerTubeHandler(DemoOptions options) : HttpMessageHandler
{
    // The type nibble in InnerTubeClient's search params ("EgWKAQI" + this + …).
    private static readonly (string Marker, string Fixture)[] SearchFixtures =
    [
        ("EgWKAQII", "search-songs.daft-punk.json"),
        ("EgWKAQIQ", "search-videos.daft-punk.json"),
        ("EgWKAQIY", "search-albums.daft-punk.json"),
        ("EgWKAQIg", "search-artists.daft-punk.json"),
        ("EgWKAQIo", "search-playlists.daft-punk.json"),
    ];

    // Continuations are routed by which recording issued the token.
    private static readonly (string IssuedBy, string Answer)[] Continuations =
    [
        ("browse-library-playlists.synthetic.json", "browse-library-playlists-continuation.synthetic.json"),
        ("browse-library-albums.synthetic.json", "browse-library-albums-continuation.synthetic.json"),
        ("browse-library-artists.synthetic.json", "browse-library-artists-continuation.synthetic.json"),
        ("browse-library-songs.synthetic.json", "browse-library-songs-continuation.synthetic.json"),
        ("browse-playlist.80s-pop.json", "browse-playlist-continuation.tracks.json"),
        ("browse-playlist-continuation.tracks.json", "browse-playlist-continuation.related-only.json"),
    ];

    // The user playlists in browse-library-playlists.synthetic.json.
    private static readonly string[] OwnedPlaylists =
    [
        "VLPLxA11yyBB22cc33DD44eeFF55gg66hhII77j",
        "VLPLzZ99xxWW88vv77UU66ttSS55rr44qqPP33o",
    ];

    // Home's later pages, in the ctoken query form: a recording's token leads to the next.
    private static readonly (string IssuedBy, string Answer)[] HomePages =
    [
        ("browse-home.json", "browse-home-continuation.synthetic.json"),
        ("browse-home-continuation.synthetic.json", "browse-home-continuation.json"),
        ("browse-home.relax.json", "browse-home-continuation.json"),
    ];

    // Mood and genre pages by their params; any other one opens Chill.
    private static readonly Dictionary<string, string> MoodPages = new()
    {
        ["ggMPOg1uX2tWZXBsRm05cHNR"] = "browse-moods-category.soundtracks.json",
        ["ggMPOg1uX2dsdVFONkZBREhy"] = "browse-moods-category.coffee-shop-blends.json",
    };

    private readonly ConcurrentDictionary<string, string> _files = new();
    private int _createdPlaylists;
    private int _addedRows;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        // Cookie rotation goes to accounts.youtube.com, not InnerTube, and its body
        // ([000,"-000…"]) isn't JSON, so the parse below would throw out of
        // session restore. A demo session has nothing to rotate: answer as YouTube
        // does when nothing needs refreshing, with no Set-Cookie.
        if (request.RequestUri?.Host == "accounts.youtube.com")
        {
            AppLog.Write("demo", $"{request.RequestUri.AbsolutePath.Trim('/')} → session");
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        var endpoint = Endpoint(request.RequestUri);
        using var body = JsonDocument.Parse(
            request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct));
        var root = body.RootElement;

        var (area, json) = Route(endpoint, request.RequestUri?.Query ?? "", root, request.Headers.Contains("Cookie"));
        AppLog.Write("demo", $"{endpoint} → {area}");

        await Task.Delay(options.Latency, ct);

        // A refused session: what YouTube Music answers once cookies have expired.
        // Cookies whose SAPISID says "expired" are always refused (the sign-in
        // dialog's error), and "session" in RAMBUTAN_DEMO_FAIL refuses every
        // signed-in request (an expiry while the app is open). Named only, never
        // part of "all": that one stands for being offline.
        if (request.Headers.TryGetValues("Cookie", out var cookies) &&
            string.Join("; ", cookies) is var cookie &&
            (cookie.Contains("SAPISID=expired", StringComparison.Ordinal) || options.FailingAreas.Contains("session")))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":{"code":401,"status":"UNAUTHENTICATED"}}"""),
            };
        }

        // Same shape as a dropped connection: no status code, just a failed send.
        if (options.Fails(area))
            throw new HttpRequestException($"Demo: '{area}' is set to fail (RAMBUTAN_DEMO_FAIL).");

        return json is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"Demo: no recording answers '{endpoint}'."),
            }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
    }

    /// <summary>"…/youtubei/v1/browse/edit_playlist?prettyPrint=false" → "browse/edit_playlist".</summary>
    private static string Endpoint(Uri? uri)
    {
        var path = uri?.AbsolutePath ?? "";
        var marker = path.IndexOf("/v1/", StringComparison.Ordinal);
        return marker < 0 ? path.Trim('/') : path[(marker + 4)..];
    }

    private (string Area, string? Json) Route(string endpoint, string query, JsonElement root, bool hasCookies)
    {
        switch (endpoint)
        {
            // A later page of a filtered search: the token rides in the query string.
            // Any filter continues with the one recorded (terminal) page.
            case "search" when query.Contains("ctoken=", StringComparison.Ordinal):
                return ("search-more", Fixture("search-songs-continuation.daft-punk.synthetic.json"));

            case "search":
                if (String(root, "query") is { } text &&
                    text.StartsWith("empty", StringComparison.OrdinalIgnoreCase))
                {
                    return ("search", """{"contents":{}}""");
                }
                // No filter: the "All" overview (top result, then mixed results).
                if (String(root, "params") is not { } filter)
                    return ("search", Fixture("search-all.daft-punk.json"));
                foreach (var (marker, fixture) in SearchFixtures)
                {
                    if (filter.StartsWith(marker, StringComparison.Ordinal))
                        return ("search", Fixture(fixture));
                }
                return ("search", Fixture("search-songs.daft-punk.json"));

            case "music/get_search_suggestions":
                return ("search", String(root, "input") is { } input &&
                                  input.StartsWith("empty", StringComparison.OrdinalIgnoreCase)
                    ? """{"contents":[]}"""
                    : Fixture("search-suggestions.daft-punk.synthetic.json"));

            case "browse" when query.Contains("ctoken=", StringComparison.Ordinal):
                foreach (var (issuedBy, answer) in HomePages)
                {
                    if (query.Contains("ctoken=" + NextToken(Fixture(issuedBy)) + "&", StringComparison.Ordinal))
                        return ("home-more", Fixture(answer));
                }
                return ("home-more", null);

            case "browse" when String(root, "continuation") is { } token:
                foreach (var (issuedBy, answer) in Continuations)
                {
                    if (Fixture(issuedBy).Contains(token, StringComparison.Ordinal))
                        return (issuedBy.Contains("library") ? "library-more" : "playlist-more", Fixture(answer));
                }
                return ("browse", null);

            case "browse":
                return (String(root, "browseId") ?? "") switch
                {
                    // Every mood chip opens the Relax recording.
                    "FEmusic_home" when String(root, "params") is not null => ("home", Fixture("browse-home.relax.json")),
                    "FEmusic_home" => ("home", Fixture("browse-home.json")),
                    "FEmusic_moods_and_genres" => ("moods", Fixture("browse-moods-and-genres.json")),
                    "FEmusic_moods_and_genres_category" => ("moods", Fixture(
                        MoodPages.GetValueOrDefault(String(root, "params") ?? "", "browse-moods-category.chill.json"))),
                    "FEmusic_liked_playlists" => ("library", Fixture("browse-library-playlists.synthetic.json")),
                    "FEmusic_liked_albums" => ("library", Fixture("browse-library-albums.synthetic.json")),
                    "FEmusic_library_corpus_artists" => ("library", Fixture("browse-library-artists.synthetic.json")),
                    "FEmusic_liked_videos" => ("library", Fixture("browse-library-songs.synthetic.json")),
                    "FEmusic_history" => ("library", Fixture("browse-history.synthetic.json")),
                    // Every track's Lyrics tab opens the one recording, whose
                    // text is invented.
                    var id when id.StartsWith("MPLY", StringComparison.Ordinal) =>
                        ("lyrics", Fixture("browse-lyrics.k8qwmwvqxgi.json")),
                    var id when id.StartsWith("MPRE", StringComparison.Ordinal) =>
                        ("album", Rated("browse-album.ram.json", "LIKE")),
                    var id when id.StartsWith("UC", StringComparison.Ordinal) =>
                        ("artist", Fixture("browse-artist.daft-punk.json")),
                    // The demo account's own playlists (its Library ones and any it
                    // creates) open as an owned playlist, which can be reordered.
                    var id when OwnedPlaylists.Contains(id) || id.StartsWith("VLPLdemo", StringComparison.Ordinal) =>
                        ("playlist", Fixture("browse-playlist.owned.synthetic.json")),
                    var id when id.StartsWith("VL", StringComparison.Ordinal) =>
                        ("playlist", Rated("browse-playlist.80s-pop.json", "LIKE", "DISLIKE")),
                    _ => ("browse", null),
                };

            case "next":
                return ("radio", Fixture("next-radio.khnokw3mw24.json"));

            case "account/account_menu":
                // Any cookies the demo accepts are the demo account, so the sign-in
                // dialog can be walked through in an anonymous demo too.
                return ("account", hasCookies ? AccountMenu : "{}");

            // Mutations succeed without remembering anything: a rename or removal
            // shows in the UI (which updates optimistically) until the page reloads.
            case "like/like" or "like/dislike" or "like/removelike" or "playlist/delete"
                or "subscription/subscribe" or "subscription/unsubscribe":
                return ("mutations", "{}");
            case "feedback":
                return ("mutations", """{"feedbackResponses":[{"isProcessed":true}]}""");
            case "browse/edit_playlist" when AddsVideo(root):
                // An add reports the new row, so Undo can move it back into place.
                var row = Interlocked.Increment(ref _addedRows);
                return ("mutations",
                    $$$"""{"status":"STATUS_SUCCEEDED","playlistEditResults":[{"playlistEditVideoAddedResultData":{"setVideoId":"DEMOSET{{{row}}}"}}]}""");
            case "browse/edit_playlist":
                return ("mutations", """{"status":"STATUS_SUCCEEDED"}""");
            case "playlist/create":
                var created = Interlocked.Increment(ref _createdPlaylists);
                return ("mutations", $$"""{"playlistId":"PLdemo{{created}}"}""");

            default:
                return (endpoint, null);
        }
    }

    private static bool AddsVideo(JsonElement root) =>
        root.TryGetProperty("actions", out var actions) &&
        actions.ValueKind == JsonValueKind.Array &&
        actions.EnumerateArray().Any(a => String(a, "action") == "ACTION_ADD_VIDEO");

    /// <summary>The token a recorded Home page hands out for the page after it.</summary>
    private static string? NextToken(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return (BrowseParsers.ParseHome(doc.RootElement).Continuation
                ?? BrowseParsers.ParseHomeContinuation(doc.RootElement).Continuation)?.Value;
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private string Fixture(string name) =>
        _files.GetOrAdd(name, n => File.ReadAllText(Path.Combine(options.FixturesDirectory, n)));

    /// <summary>
    /// The recordings are anonymous, so every row says INDIFFERENT. Signed in, the
    /// demo account has rated the first rows (in order, one status each) so rated
    /// thumbs can be seen. Presses aren't remembered here; the app's own
    /// TrackRatings holds them for the session, as it does against the real server.
    /// </summary>
    private string Rated(string name, params string[] statuses)
    {
        if (!options.SignedIn)
            return Fixture(name);

        return _files.GetOrAdd(name + "#rated", _ =>
        {
            const string unrated = "\"likeStatus\":\"INDIFFERENT\"";
            var json = new StringBuilder(Fixture(name));
            var from = 0;
            foreach (var status in statuses)
            {
                var at = json.ToString().IndexOf(unrated, from, StringComparison.Ordinal);
                if (at < 0)
                    break;
                var rated = $"\"likeStatus\":\"{status}\"";
                json.Remove(at, unrated.Length).Insert(at, rated);
                from = at + rated.Length;
            }
            return json.ToString();
        });
    }

    // The one path InnerTubeClient.GetSessionInfoAsync reads.
    private static readonly string AccountMenu =
        """{"actions":[{"openPopupAction":{"popup":{"multiPageMenuRenderer":{"header":{"activeAccountHeaderRenderer":{"accountName":{"runs":[{"text":"NAME"}]},"accountPhoto":{"thumbnails":[{"url":"https://yt3.ggpht.com/demo-avatar=s88","width":88,"height":88}]}}}}}}}]}"""
            .Replace("NAME", DemoOptions.AccountName);
}
