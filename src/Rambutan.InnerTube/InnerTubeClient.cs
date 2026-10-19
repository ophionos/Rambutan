using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Rambutan.Core.Catalog;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube;

public sealed class InnerTubeClient : ICatalog, IDisposable
{
    private const string BaseUrl = "https://music.youtube.com/youtubei/v1/";
    private const string ClientName = "WEB_REMIX";
    private const string ClientVersion = "1.20260708.01.00";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    // Pre-computed search filter params (protobuf, type nibble varies), verified live 2026-07-12.
    private static readonly Dictionary<SearchFilter, string> FilterParams = new()
    {
        [SearchFilter.Songs] = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D",
        [SearchFilter.Videos] = "EgWKAQIQAWoKEAkQBRAKEAMQBA%3D%3D",
        [SearchFilter.Albums] = "EgWKAQIYAWoKEAkQBRAKEAMQBA%3D%3D",
        [SearchFilter.Artists] = "EgWKAQIgAWoKEAkQBRAKEAMQBA%3D%3D",
        [SearchFilter.Playlists] = "EgWKAQIoAWoKEAkQBRAKEAMQBA%3D%3D",
    };

    private readonly HttpClient _http;
    // The sign-in redirects set cookies on each hop, which a handler that follows
    // redirects itself would drop. A client that was passed in is used as it is.
    private readonly HttpClient _navigation;
    private readonly bool _ownsHttp;
    private const string RotateCookiesUrl = "https://accounts.youtube.com/RotateCookies";

    // The link behind YouTube's own "Sign in" button. passive=true has Google
    // answer from the sign-in it already has, never with a form.
    private static readonly Uri ReissueUri = new(
        "https://accounts.google.com/ServiceLogin?service=youtube&passive=true&continue=" +
        Uri.EscapeDataString("https://www.youtube.com/signin?action_handle_signin=true&app=desktop&hl=en&next=" +
            Uri.EscapeDataString("https://music.youtube.com/")));

    // The hosts Google's sign-in redirects pass through on the way to music.
    private static readonly HashSet<string> ReissueHosts =
        [GoogleSignIn.Host, "accounts.youtube.com", "www.youtube.com"];

    // Three hops in practice (accounts.google.com, SetSID, youtube.com/signin).
    private const int MaxReissueHops = 8;

    private volatile InnerTubeSession? _session;
    private volatile GoogleSignIn? _google;
    // Serializes read-merge-write of _session, so two responses that both set
    // cookies can't lose one update.
    private readonly Lock _sessionLock = new();
    // The visitor the last Home page was issued to; its continuations need it.
    private volatile string? _homeVisitorData;
    // Cookie names YouTube Music has set on an authed response, logged once each.
    private readonly HashSet<string> _loggedCookieNames = [];

    public InnerTubeClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        // UseCookies must be off: SocketsHttpHandler silently drops a manual
        // per-request Cookie header when its own cookie container is active.
        _http = http ?? new HttpClient(new SocketsHttpHandler { UseCookies = false });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Add("Origin", "https://music.youtube.com");
        if (_ownsHttp)
        {
            _navigation = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
            _navigation.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }
        else
        {
            _navigation = _http;
        }
    }

    public InnerTubeSession? Session => _session;

    public void SetSession(InnerTubeSession? session)
    {
        lock (_sessionLock)
            _session = session;
    }

    /// <summary>The Google sign-in that <see cref="ReissueSessionAsync"/> renews
    /// the session from; null when the user didn't give one.</summary>
    public GoogleSignIn? GoogleSignIn => _google;

    public void SetGoogleSignIn(GoogleSignIn? google)
    {
        lock (_sessionLock)
            _google = google;
    }

    /// <summary>
    /// The Google sign-in's own cookies changed during <see cref="ReissueSessionAsync"/>.
    /// Carries the new copy, which is already the client's. Raised on the thread
    /// that made the request.
    /// </summary>
    public event Action<GoogleSignIn>? GoogleSignInUpdated;

    /// <summary>
    /// A response set cookies that changed the current session: Google rotated
    /// some of them. Carries the refreshed session, which is already the client's.
    /// Raised on the thread that made the request; the app stores it, so the next
    /// launch starts from the fresh values rather than the pasted ones.
    /// </summary>
    public event Action<InnerTubeSession>? SessionUpdated;

    /// <summary>
    /// Asks Google for fresh values of the session's rotating cookies
    /// (__Secure-1PSIDTS / __Secure-3PSIDTS), as a signed-in browser tab does
    /// from time to time. Google rejects old values soon after it rotates them,
    /// so a stored copy that is never refreshed expires. The answer's cookies go
    /// through the same merge as any response's, and <see cref="SessionUpdated"/>
    /// reports the result. True when the call succeeded, whether or not
    /// anything changed.
    /// </summary>
    public async Task<bool> RotateCookiesAsync(CancellationToken ct = default)
    {
        if (_session is not { } session)
            return false;

        using var request = new HttpRequestMessage(HttpMethod.Post, RotateCookiesUrl)
        {
            Content = new StringContent("""[000,"-0000000000000000000"]""", Encoding.UTF8,
                new MediaTypeHeaderValue("application/json")),
        };
        request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
        request.Headers.Referrer = new Uri("https://music.youtube.com/");

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var updated = ApplySetCookies(session, response);
            AppLog.Write("session", response.IsSuccessStatusCode
                ? $"rotate: {(updated ? "cookies refreshed" : "nothing changed")}"
                : $"rotate: refused ({(int)response.StatusCode} {response.StatusCode})");
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            AppLog.Write("session", $"rotate: unreachable ({e.GetType().Name}: {e.Message})");
            return false;
        }
    }

    /// <summary>
    /// Has Google issue the session new youtube.com cookies from the
    /// <see cref="GoogleSignIn"/>, as it does when a browser whose YouTube cookies
    /// have expired opens YouTube: accounts.google.com redirects through
    /// accounts.youtube.com and www.youtube.com back to music, and each hop sets
    /// cookies. Only accounts.google.com gets the Google cookies; the youtube.com
    /// hops get none, so every youtube.com cookie of the result comes from Google.
    /// The new cookies become the session (the same sign-in, reported by
    /// <see cref="SessionUpdated"/>), unless it was signed out or replaced
    /// meanwhile. True when Google issued them; false when it asked for a sign-in
    /// instead, couldn't be reached, or there is no session or Google sign-in.
    /// </summary>
    public async Task<bool> ReissueSessionAsync(CancellationToken ct = default)
    {
        if (_session is not { } session || _google is not { } started)
            return false;

        var google = started;
        var cookies = new List<(string Name, string Value)>();
        var url = ReissueUri;
        try
        {
            for (var hop = 0; hop < MaxReissueHops; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
                if (url.Host == GoogleSignIn.Host)
                    request.Headers.TryAddWithoutValidation("Cookie", google.CookieHeader);

                using var response = await _navigation.SendAsync(request, ct);
                if (response.Headers.TryGetValues("Set-Cookie", out var headers))
                {
                    var now = DateTimeOffset.UtcNow;
                    google = google.WithSetCookies(headers, url.Host, now);
                    CookiePairs.Merge(cookies, headers, InnerTubeSession.MusicHost, url.Host, now);
                }

                // On Linux a relative "/path" parses as an absolute file: URI.
                var next = response.Headers.Location is { } location
                    ? location.IsAbsoluteUri && !location.IsFile ? location : new Uri(url, location.OriginalString)
                    : null;
                if ((int)response.StatusCode is < 300 or > 399 || next is null ||
                    (next.Host != InnerTubeSession.MusicHost && !ReissueHosts.Contains(next.Host)))
                {
                    // Signed out, Google sends the browser to a sign-in page
                    // (/v3/signin/identifier) instead. Path only: the query carries tokens.
                    var to = next is null ? "" : $" -> {next.Host}{next.AbsolutePath}";
                    AppLog.Write("session", $"reissue: refused ({url.Host}{url.AbsolutePath} {(int)response.StatusCode}{to})");
                    return Finish(null);
                }
                if (next.Host == InnerTubeSession.MusicHost)
                {
                    if (session.Reissued(cookies) is { } issued)
                        return Finish(issued);
                    AppLog.Write("session", "reissue: refused (no SAPISID among the cookies Google set)");
                    return Finish(null);
                }
                url = next;
            }

            AppLog.Write("session", "reissue: refused (too many redirects)");
            return Finish(null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            AppLog.Write("session", $"reissue: unreachable ({e.GetType().Name}: {e.Message})");
            return Finish(null);
        }

        bool Finish(InnerTubeSession? issued)
        {
            InnerTubeSession? updated = null;
            var googleChanged = false;
            lock (_sessionLock)
            {
                // Google may refresh its own cookies even when it issues nothing;
                // they're kept unless the sign-in was replaced meanwhile.
                if (ReferenceEquals(_google, started) && !ReferenceEquals(google, started))
                {
                    _google = google;
                    googleChanged = true;
                }
                if (issued is not null && _session is { } current && InnerTubeSession.IsSameSignIn(current, session))
                    _session = updated = issued;
            }

            if (googleChanged)
                GoogleSignInUpdated?.Invoke(google);
            if (updated is null)
                return false;
            AppLog.Write("session", "reissue: new session issued");
            SessionUpdated?.Invoke(updated);
            return true;
        }
    }

    /// <summary>
    /// Merges the cookies <paramref name="response"/> set into the current
    /// session, when the request was made with the same sign-in. A response to
    /// a signed-out or replaced session changes nothing: it must not bring the
    /// old cookies back. True when the session changed.
    /// </summary>
    private bool ApplySetCookies(InnerTubeSession sentWith, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
            return false;

        var host = response.RequestMessage?.RequestUri?.Host ?? "";
        InnerTubeSession updated;
        lock (_sessionLock)
        {
            if (_session is not { } current || !InnerTubeSession.IsSameSignIn(current, sentWith))
                return false;
            // Merged into the current session, not the one the request carried: a
            // response that raced a newer update must not undo it.
            if (current.WithSetCookies(headers, host, DateTimeOffset.UtcNow) is not { } merged ||
                ReferenceEquals(merged, current))
            {
                return false;
            }
            _session = updated = merged;
        }

        SessionUpdated?.Invoke(updated);
        return true;
    }

    /// <summary>
    /// YouTube Music refused a request made with the current session (401/403).
    /// Raised on the thread that made the request. The app re-probes the session
    /// so an expiry mid-session surfaces as a sign-in prompt, not only as one
    /// page's error.
    /// </summary>
    public event Action? SessionRejected;

    /// <summary>
    /// Probes session health: expired cookies must surface as an
    /// actionable state, never as silently-empty authed surfaces.
    /// </summary>
    public Task<SessionInfo> GetSessionInfoAsync(CancellationToken ct = default) =>
        _session is { } session
            ? GetSessionInfoAsync(session, ct)
            : Task.FromResult(new SessionInfo(SessionState.Anonymous, null));

    /// <summary>
    /// Probes <paramref name="session"/> without making it the client's session,
    /// so the sign-in dialog can check pasted cookies while the current session
    /// (or none) stays in place until they prove good.
    /// </summary>
    public async Task<SessionInfo> GetSessionInfoAsync(InnerTubeSession session, CancellationToken ct)
    {
        try
        {
            var request = new BrowseRequest(Context(), BrowseId: null);
            using var doc = await PostAsync(
                "account/account_menu", request, InnerTubeJsonContext.Default.BrowseRequest, session, ct);

            if (doc.RootElement.TryGetPath(out var header,
                    "actions", "0", "openPopupAction", "popup", "multiPageMenuRenderer",
                    "header", "activeAccountHeaderRenderer") &&
                ParseHelpers.RunsText(header, "accountName") is { } name)
            {
                AppLog.Write("session", "check: valid");
                return new SessionInfo(SessionState.Valid, name, AccountPhoto(header));
            }

            // A 200 without an active account: YouTube Music answered as if signed
            // out. The top-level keys tell that apart from a changed response shape.
            var keys = doc.RootElement.ValueKind is JsonValueKind.Object
                ? string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name))
                : doc.RootElement.ValueKind.ToString();
            AppLog.Write("session", $"check: expired (account menu has no active account; keys: {keys})");
            return new SessionInfo(SessionState.Expired, null);
        }
        catch (HttpRequestException e) when (
            e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            AppLog.Write("session", $"check: expired ({(int)e.StatusCode!} {e.StatusCode})");
            return new SessionInfo(SessionState.Expired, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Offline, server hiccup, or a garbled response — none of these prove
            // the cookies are bad, so never report the session as expired for them.
            AppLog.Write("session", $"check: unreachable ({e.GetType().Name}: {e.Message})");
            return new SessionInfo(SessionState.Unreachable, null);
        }
    }

    /// <summary>The largest of the account header's avatar sizes. Optional: a
    /// header without one is still a valid session.</summary>
    private static string? AccountPhoto(JsonElement header) =>
        header.TryGetPath(out var thumbs, "accountPhoto", "thumbnails") &&
        thumbs.ValueKind == JsonValueKind.Array && thumbs.GetArrayLength() > 0 &&
        thumbs[thumbs.GetArrayLength() - 1].TryGetProperty("url", out var url) &&
        url.ValueKind == JsonValueKind.String
            ? url.GetString()
            : null;

    public string Language { get; init; } = "en";
    public string Region { get; init; } = "US";

    public async Task<IReadOnlyList<TrackInfo>> SearchSongsAsync(
        string query, CancellationToken ct = default)
    {
        var entries = await SearchAsync(query, SearchFilter.Songs, ct);
        return entries.OfType<SongEntry>().Select(e => e.Track).ToArray();
    }

    public async Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        string query, SearchFilter filter, CancellationToken ct = default) =>
        (await SearchPagedAsync(query, filter, ct)).Items;

    public async Task<SearchOverview> SearchAllAsync(string query, CancellationToken ct = default)
    {
        using var doc = await RecordSearchAsync(query, filter: null, ct: ct);
        return SearchResponseParser.ParseOverview(doc.RootElement);
    }

    public async Task<SearchPage> SearchPagedAsync(
        string query, SearchFilter filter, CancellationToken ct = default)
    {
        using var doc = await RecordSearchAsync(query, filter, ct: ct);
        return SearchResponseParser.ParsePage(doc.RootElement);
    }

    public async Task<SearchPage> GetSearchContinuationAsync(
        string query, SearchFilter filter, ContinuationToken continuation, CancellationToken ct = default)
    {
        using var doc = await RecordSearchAsync(query, filter, continuation.Value, ct);
        return SearchResponseParser.ParseContinuation(doc.RootElement);
    }

    public async Task<SearchSuggestions> GetSearchSuggestionsAsync(
        string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return SearchSuggestions.Empty;

        using var doc = await RecordSearchSuggestionsAsync(query, ct);
        return SearchResponseParser.ParseSuggestions(doc.RootElement);
    }

    public async Task<HomePage> GetHomeAsync(CancellationToken ct = default)
    {
        using var doc = await BrowseAsync("FEmusic_home", ct);
        _homeVisitorData = VisitorData(doc.RootElement);
        return BrowseParsers.ParseHome(doc.RootElement);
    }

    public async Task<HomePage> GetHomeAsync(HomeChip chip, CancellationToken ct = default)
    {
        using var doc = await BrowseAsync("FEmusic_home", ct, chip.Params);
        _homeVisitorData = VisitorData(doc.RootElement);
        return BrowseParsers.ParseHome(doc.RootElement);
    }

    public async Task<HomePage> GetHomeContinuationAsync(
        ContinuationToken continuation, CancellationToken ct = default)
    {
        using var doc = await RecordSectionContinuationAsync(continuation.Value, _homeVisitorData, ct);
        return BrowseParsers.ParseHomeContinuation(doc.RootElement);
    }

    /// <summary>The visitor YTM assigned in a response; anonymous tokens only work for it.</summary>
    internal static string? VisitorData(JsonElement root) =>
        root.TryGetPath(out var visitor, "responseContext", "visitorData") ? visitor.GetString() : null;

    public async Task<MoodsPage> GetMoodsAndGenresAsync(CancellationToken ct = default)
    {
        using var doc = await BrowseAsync("FEmusic_moods_and_genres", ct);
        return BrowseParsers.ParseMoodsAndGenres(doc.RootElement);
    }

    public async Task<MoodCategoryPage> GetMoodCategoryAsync(
        MoodCategory category, CancellationToken ct = default)
    {
        using var doc = await BrowseAsync(BrowseParsers.MoodCategoryBrowseId, ct, category.Params);
        return BrowseParsers.ParseMoodCategory(doc.RootElement);
    }

    public async Task<AlbumPage> GetAlbumAsync(string browseId, CancellationToken ct = default)
    {
        using var doc = await BrowseAsync(browseId, ct);
        var album = BrowseParsers.ParseAlbum(browseId, doc.RootElement);
        // Signed out, the header's toggle always reads "not saved"; that's unknown, not false.
        return _session is null ? album with { InLibrary = null } : album;
    }

    public async Task<PlaylistPage> GetPlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        var browseId = playlistId.StartsWith("VL", StringComparison.Ordinal)
            ? playlistId
            : "VL" + playlistId;
        using var doc = await BrowseAsync(browseId, ct);
        return BrowseParsers.ParsePlaylist(playlistId, doc.RootElement);
    }

    public async Task<PlaylistPage> GetPlaylistContinuationAsync(
        string playlistId, ContinuationToken continuation, CancellationToken ct = default)
    {
        var request = new BrowseRequest(Context(), BrowseId: null, continuation.Value);
        using var doc = await PostAsync(
            "browse", request, InnerTubeJsonContext.Default.BrowseRequest, ct);
        return BrowseParsers.ParsePlaylistContinuation(playlistId, doc.RootElement);
    }

    public async Task<ArtistPage> GetArtistAsync(string browseId, CancellationToken ct = default)
    {
        using var doc = await BrowseAsync(browseId, ct);
        var artist = BrowseParsers.ParseArtist(browseId, doc.RootElement);
        // Signed out, the button always reads "not subscribed"; that's unknown, not false.
        return _session is null ? artist with { Subscribed = null } : artist;
    }

    public async Task<IReadOnlyList<TrackInfo>> GetRadioAsync(
        string videoId, CancellationToken ct = default)
    {
        using var doc = await NextAsync(videoId, ct);
        return NextResponseParser.ParseWatchQueue(doc.RootElement);
    }

    public async Task<IReadOnlyList<TrackInfo>> GetMixAsync(MixSeed seed, CancellationToken ct = default)
    {
        using var doc = await NextAsync(seed.VideoId, ct, seed.PlaylistId, seed.Params);
        return NextResponseParser.ParseWatchQueue(doc.RootElement);
    }

    public async Task<Rating?> GetRatingAsync(string videoId, CancellationToken ct = default)
    {
        // Anonymous responses always say INDIFFERENT, which would read as a real
        // "not rated"; signed out, the honest answer is "unknown" and costs nothing.
        if (_session is null)
            return null;

        // The same request the web player makes on every track; its player
        // overlay carries the like button for the requested video.
        using var doc = await NextAsync(videoId, ct);
        return NextResponseParser.ParseRating(doc.RootElement, videoId);
    }

    public Task<LibraryPage> GetLibraryPlaylistsAsync(CancellationToken ct = default) =>
        LibraryBrowseAsync("FEmusic_liked_playlists", LibraryParsers.ParseLibraryPlaylists, ct);

    public Task<LibraryPage> GetLibraryPlaylistsContinuationAsync(
        ContinuationToken continuation, CancellationToken ct = default) =>
        LibraryContinuationAsync(continuation, LibraryParsers.ParseLibraryGridContinuation, ct);

    public Task<LibraryPage> GetLibraryAlbumsAsync(CancellationToken ct = default) =>
        LibraryBrowseAsync("FEmusic_liked_albums", LibraryParsers.ParseLibraryAlbums, ct);

    public Task<LibraryPage> GetLibraryAlbumsContinuationAsync(
        ContinuationToken continuation, CancellationToken ct = default) =>
        LibraryContinuationAsync(continuation, LibraryParsers.ParseLibraryGridContinuation, ct);

    public Task<LibraryPage> GetLibraryArtistsAsync(CancellationToken ct = default) =>
        LibraryBrowseAsync("FEmusic_library_corpus_artists", LibraryParsers.ParseLibraryArtists, ct);

    public Task<LibraryPage> GetLibraryArtistsContinuationAsync(
        ContinuationToken continuation, CancellationToken ct = default) =>
        LibraryContinuationAsync(continuation, LibraryParsers.ParseLibraryArtistsContinuation, ct);

    public Task<LibraryPage> GetLibrarySongsAsync(CancellationToken ct = default) =>
        LibraryBrowseAsync("FEmusic_liked_videos", LibraryParsers.ParseLibrarySongs, ct);

    public Task<LibraryPage> GetLibrarySongsContinuationAsync(
        ContinuationToken continuation, CancellationToken ct = default) =>
        LibraryContinuationAsync(continuation, LibraryParsers.ParseLibrarySongsContinuation, ct);

    public async Task SetSongInLibraryAsync(
        LibraryToggle toggle, bool inLibrary, CancellationToken ct = default)
    {
        // ytmusicapi's edit_song_library_status: the row menu's token, sent as feedback.
        RequireSession();
        var request = new FeedbackRequest(
            Context(), [inLibrary ? toggle.AddToken : toggle.RemoveToken]);
        using var doc = await PostAsync(
            "feedback", request, InnerTubeJsonContext.Default.FeedbackRequest, ct);
        // A stale or foreign token answers 200 with isProcessed false.
        if (LibraryParsers.IsFeedbackProcessed(doc.RootElement) is false)
            throw new InvalidOperationException("YouTube Music didn't apply the library change.");
    }

    public async Task SetAlbumInLibraryAsync(
        string audioPlaylistId, bool inLibrary, CancellationToken ct = default)
    {
        // What the album toggle's likeEndpoint sends (ytmusicapi's rate_playlist):
        // LIKE saves, INDIFFERENT removes.
        RequireSession();
        var request = new RateRequest(Context(), new RateTarget(PlaylistId: audioPlaylistId));
        using var doc = await PostAsync(
            inLibrary ? "like/like" : "like/removelike",
            request, InnerTubeJsonContext.Default.RateRequest, ct);
    }

    public async Task SetPlaylistInLibraryAsync(
        string playlistId, bool inLibrary, CancellationToken ct = default)
    {
        // The album toggle's request with the playlist's own id: the like
        // endpoints take the id without the VL that its browse id carries.
        RequireSession();
        var id = playlistId.StartsWith("VL", StringComparison.Ordinal) ? playlistId[2..] : playlistId;
        var request = new RateRequest(Context(), new RateTarget(PlaylistId: id));
        using var doc = await PostAsync(
            inLibrary ? "like/like" : "like/removelike",
            request, InnerTubeJsonContext.Default.RateRequest, ct);
    }

    public async Task SetArtistSubscribedAsync(
        string artistBrowseId, bool subscribed, CancellationToken ct = default)
    {
        RequireSession();
        var request = new SubscriptionRequest(Context(), [LibraryParsers.ChannelId(artistBrowseId)]);
        using var doc = await PostAsync(
            subscribed ? "subscription/subscribe" : "subscription/unsubscribe",
            request, InnerTubeJsonContext.Default.SubscriptionRequest, ct);
    }

    private async Task<LibraryPage> LibraryBrowseAsync(
        string browseId, Func<JsonElement, LibraryPage> parse, CancellationToken ct)
    {
        RequireSession();
        using var doc = await BrowseAsync(browseId, ct);
        return parse(doc.RootElement);
    }

    private async Task<LibraryPage> LibraryContinuationAsync(
        ContinuationToken continuation, Func<JsonElement, LibraryPage> parse, CancellationToken ct)
    {
        RequireSession();
        var request = new BrowseRequest(Context(), BrowseId: null, continuation.Value);
        using var doc = await PostAsync(
            "browse", request, InnerTubeJsonContext.Default.BrowseRequest, ct);
        return parse(doc.RootElement);
    }

    public async Task RateAsync(string videoId, Rating rating, CancellationToken ct = default)
    {
        RequireSession();
        var endpoint = rating switch
        {
            Rating.Like => "like/like",
            Rating.Dislike => "like/dislike",
            _ => "like/removelike",
        };
        var request = new RateRequest(Context(), new RateTarget(VideoId: videoId));
        using var doc = await PostAsync(
            endpoint, request, InnerTubeJsonContext.Default.RateRequest, ct);
    }

    public async Task<string> CreatePlaylistAsync(
        string title, string? description = null,
        PlaylistPrivacy privacy = PlaylistPrivacy.Private, CancellationToken ct = default)
    {
        RequireSession();
        var request = new CreatePlaylistRequest(Context(), title, description, privacy switch
        {
            PlaylistPrivacy.Public => "PUBLIC",
            PlaylistPrivacy.Unlisted => "UNLISTED",
            _ => "PRIVATE",
        });
        using var doc = await PostAsync(
            "playlist/create", request, InnerTubeJsonContext.Default.CreatePlaylistRequest, ct);
        return LibraryParsers.ParseCreatedPlaylistId(doc.RootElement);
    }

    public async Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        RequireSession();
        var request = new DeletePlaylistRequest(Context(), StripVl(playlistId));
        using var doc = await PostAsync(
            "playlist/delete", request, InnerTubeJsonContext.Default.DeletePlaylistRequest, ct);
    }

    public Task RenamePlaylistAsync(string playlistId, string title, CancellationToken ct = default) =>
        EditPlaylistAsync(
            playlistId, [new PlaylistEditAction("ACTION_SET_PLAYLIST_NAME", PlaylistName: title)], ct);

    public Task AddToPlaylistAsync(
        string playlistId, IReadOnlyList<string> videoIds, CancellationToken ct = default) =>
        EditPlaylistAsync(
            playlistId,
            videoIds.Select(v => new PlaylistEditAction("ACTION_ADD_VIDEO", AddedVideoId: v)).ToArray(),
            ct);

    public Task RemoveFromPlaylistAsync(
        string playlistId, string videoId, string setVideoId, CancellationToken ct = default) =>
        EditPlaylistAsync(
            playlistId,
            [new PlaylistEditAction("ACTION_REMOVE_VIDEO", RemovedVideoId: videoId, SetVideoId: setVideoId)],
            ct);

    public Task MovePlaylistItemAsync(
        string playlistId, string setVideoId, string? beforeSetVideoId, CancellationToken ct = default) =>
        // ytmusicapi's edit_playlist(moveItem=…): without a successor the row goes last.
        EditPlaylistAsync(
            playlistId,
            [new PlaylistEditAction(
                "ACTION_MOVE_VIDEO_BEFORE", SetVideoId: setVideoId, MovedSetVideoIdSuccessor: beforeSetVideoId)],
            ct);

    public async Task<string?> InsertIntoPlaylistAsync(
        string playlistId, string videoId, string? beforeSetVideoId, CancellationToken ct = default)
    {
        // Two edits, because the new row's setVideoId only exists once the add is done.
        var added = await EditPlaylistAsync(
            playlistId, [new PlaylistEditAction("ACTION_ADD_VIDEO", AddedVideoId: videoId)], ct,
            LibraryParsers.ParseAddedSetVideoId);
        if (added is null || beforeSetVideoId is null)
            return added;

        await MovePlaylistItemAsync(playlistId, added, beforeSetVideoId, ct);
        return added;
    }

    private Task EditPlaylistAsync(
        string playlistId, IReadOnlyList<PlaylistEditAction> actions, CancellationToken ct) =>
        EditPlaylistAsync<object?>(playlistId, actions, ct, static _ => null);

    private async Task<T> EditPlaylistAsync<T>(
        string playlistId, IReadOnlyList<PlaylistEditAction> actions, CancellationToken ct,
        Func<JsonElement, T> read)
    {
        RequireSession();
        var request = new EditPlaylistRequest(Context(), StripVl(playlistId), actions);
        using var doc = await PostAsync(
            "browse/edit_playlist", request, InnerTubeJsonContext.Default.EditPlaylistRequest, ct);
        if (!LibraryParsers.IsEditSuccess(doc.RootElement))
            throw new InvalidOperationException("YouTube Music rejected the playlist edit.");
        return read(doc.RootElement);
    }

    /// <summary>Mutation endpoints want the bare playlistId, never the VL-prefixed browseId.</summary>
    private static string StripVl(string playlistId) =>
        playlistId.StartsWith("VL", StringComparison.Ordinal) ? playlistId[2..] : playlistId;

    private void RequireSession()
    {
        if (_session is null)
            throw new NotSignedInException();
    }

    /// <summary>Raw browse response for fixture recording; the caller owns the document.</summary>
    internal Task<JsonDocument> RecordBrowseAsync(
        string? browseId, string? continuation = null, string? browseParams = null,
        CancellationToken ct = default)
    {
        var request = new BrowseRequest(Context(), browseId, continuation, browseParams);
        return PostAsync("browse", request, InnerTubeJsonContext.Default.BrowseRequest, ct);
    }

    /// <summary>
    /// Raw browse continuation sent as ctoken/continuation query parameters, the
    /// form a sectionListRenderer's nextContinuationData needs (Home's later
    /// shelves; ytmusicapi's get_continuations). The body form that playlists use
    /// returns an empty tab for these tokens, and so does a signed-out request
    /// without <paramref name="visitorData"/> from the page that issued the token.
    /// </summary>
    internal Task<JsonDocument> RecordSectionContinuationAsync(
        string continuation, string? visitorData, CancellationToken ct = default)
    {
        var request = new BrowseRequest(
            new RequestContext(new ClientInfo(ClientName, ClientVersion, Language, Region, visitorData)),
            BrowseId: null);
        return PostAsync(
            $"browse?ctoken={continuation}&continuation={continuation}&type=next",
            request, InnerTubeJsonContext.Default.BrowseRequest, ct);
    }

    /// <summary>
    /// Raw search response (also for fixture recording); the caller owns the
    /// document. A continuation page repeats the first page's body and adds the
    /// token as ctoken/continuation query parameters, as ytmusicapi's
    /// get_continuations does for search (get_continuation_string).
    /// </summary>
    internal Task<JsonDocument> RecordSearchAsync(
        string query, SearchFilter? filter = null, string? continuation = null,
        CancellationToken ct = default)
    {
        var request = new SearchRequest(Context(), query, filter is { } f ? FilterParams[f] : null);
        var endpoint = continuation is null
            ? "search"
            : $"search?ctoken={continuation}&continuation={continuation}";
        return PostAsync(endpoint, request, InnerTubeJsonContext.Default.SearchRequest, ct);
    }

    /// <summary>Raw search-suggestions response; also used for fixture recording.</summary>
    internal Task<JsonDocument> RecordSearchSuggestionsAsync(string query, CancellationToken ct = default)
    {
        var request = new SearchSuggestionsRequest(Context(), query);
        return PostAsync(
            "music/get_search_suggestions", request,
            InnerTubeJsonContext.Default.SearchSuggestionsRequest, ct);
    }

    private Task<JsonDocument> BrowseAsync(string browseId, CancellationToken ct, string? browseParams = null)
    {
        var request = new BrowseRequest(Context(), browseId, Params: browseParams);
        return PostAsync("browse", request, InnerTubeJsonContext.Default.BrowseRequest, ct);
    }

    /// <summary>Raw next (watch-queue) response; also used for fixture recording.</summary>
    /// <param name="playlistId">The mix to build; null for the track's own radio.
    /// RDAMVM{videoId} is the per-track radio playlist the web player uses.</param>
    internal Task<JsonDocument> NextAsync(
        string videoId, CancellationToken ct = default, string? playlistId = null, string? @params = null)
    {
        var request = new NextRequest(Context(), videoId, playlistId ?? "RDAMVM" + videoId,
            EnablePersistentPlaylistPanel: true, IsAudioOnly: true,
            TunerSettingValue: "AUTOMIX_SETTING_NORMAL", Params: @params);
        return PostAsync("next", request, InnerTubeJsonContext.Default.NextRequest, ct);
    }

    private RequestContext Context() =>
        new(new ClientInfo(ClientName, ClientVersion, Language, Region));

    private Task<JsonDocument> PostAsync<TRequest>(
        string endpoint,
        TRequest body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> typeInfo,
        CancellationToken ct) =>
        PostAsync(endpoint, body, typeInfo, _session, ct);

    private async Task<JsonDocument> PostAsync<TRequest>(
        string endpoint,
        TRequest body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> typeInfo,
        InnerTubeSession? session,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, typeInfo);
        var separator = endpoint.Contains('?') ? '&' : '?';
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{BaseUrl}{endpoint}{separator}prettyPrint=false")
        {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };

        if (session is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
            request.Headers.TryAddWithoutValidation(
                "Authorization", session.ComputeAuthorization(DateTimeOffset.UtcNow));
            request.Headers.TryAddWithoutValidation("X-Origin", "https://music.youtube.com");
        }

        using var response = await _http.SendAsync(request, ct);
        if (session is not null)
        {
            LogCookieNames(endpoint, response);
            ApplySetCookies(session, response);
        }
        if (!response.IsSuccessStatusCode)
        {
            // InnerTube errors carry a JSON body saying why — keep an excerpt.
            var error = await response.Content.ReadAsStringAsync(ct);
            if (session is not null && InnerTubeSession.IsSameSignIn(session, _session) &&
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                SessionRejected?.Invoke();
            }
            throw new HttpRequestException(
                $"InnerTube '{endpoint}' returned {(int)response.StatusCode} " +
                $"({response.StatusCode}): {(error.Length > 256 ? error[..256] + "…" : error)}",
                inner: null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>
    /// Logs the names (never the values) of cookies an authed response sets, once
    /// per name, so the log shows which API responses rotate anything.
    /// </summary>
    private void LogCookieNames(string endpoint, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return;

        List<string> fresh = [];
        lock (_loggedCookieNames)
        {
            foreach (var value in values)
            {
                var eq = value.IndexOf('=');
                if (eq > 0 && _loggedCookieNames.Add(value[..eq].Trim()))
                    fresh.Add(value[..eq].Trim());
            }
        }

        if (fresh.Count > 0)
            AppLog.Write("session", $"'{endpoint}' response sets cookies: {string.Join(", ", fresh)}");
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
            _navigation.Dispose();
        }
    }
}
