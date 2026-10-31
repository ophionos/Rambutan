using System.Net;
using System.Text;
using System.Text.Json;
using Rambutan.Core.Import;
using Rambutan.Integrations.Spotify;

namespace Rambutan.Integrations.Tests.Spotify;

/// <summary>
/// The embed pages are recorded (their anonymous key, correlation id and
/// Sentry trace scrubbed). The web player API's page is hand-made in the shape
/// its playlist query answers: recording it is the maintainer's
/// (<c>FixtureRecorder -- spotify</c>).
/// </summary>
public class SpotifyLinkReaderTests
{
    private static readonly SpotifyListLink TopHits = new(SpotifyListKind.Playlist, "37i9dQZF1DXcBWIGoYBM5M");
    private static readonly SpotifyListLink AllOut = new(SpotifyListKind.Playlist, "37i9dQZF1DX5Ejj0EkURtP");
    private static readonly SpotifyListLink Album = new(SpotifyListKind.Album, "4aawyAB9vmqN3uQ7FjRGTy");

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Spotify", name));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }

    private static HttpResponseMessage Answer(string body, HttpStatusCode status = HttpStatusCode.OK, string type = "text/html") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    [Fact]
    public void An_embed_page_lists_the_playlist_in_order()
    {
        var embed = SpotifyLinkReader.ParseEmbed(Fixture("embed.playlist.html"), 200, TopHits);
        var list = embed.List;

        Assert.Equal("Today’s Top Hits", list.Name);
        Assert.Equal(ImportedListKind.Playlist, list.Kind);
        Assert.Equal(TopHits.Uri, list.Source);
        Assert.False(list.Incomplete);
        Assert.Equal(50, list.Tracks.Count);
        Assert.Equal(0, list.Skipped);
        var first = list.Tracks[0];
        Assert.Equal("Loser", first.Title);
        Assert.Equal(["Tame Impala"], first.Artists);
        Assert.Equal("7bxaFZ1O3cHkgLKMsdC3xR", first.SpotifyId);
        Assert.Equal(TimeSpan.FromMilliseconds(223069), first.Duration);
        Assert.Null(first.Album);
        Assert.Equal("REDACTED", embed.AccessToken);
    }

    [Fact]
    public void Artists_are_split_where_the_page_separates_them_not_on_every_comma()
    {
        var tracks = SpotifyLinkReader.ParseEmbed(Fixture("embed.playlist-100.html"), 200, AllOut).List.Tracks;

        Assert.Equal(["R. City", "Adam Levine"], tracks[^1].Artists);
        Assert.Contains(tracks, t => t.Artists.SequenceEqual(["Tyler, The Creator", "Kali Uchis"]));
    }

    [Fact]
    public void An_albums_songs_take_its_name_as_their_album()
    {
        var list = SpotifyLinkReader.ParseEmbed(Fixture("embed.album.html"), 200, Album).List;

        Assert.Equal("Global Warming", list.Name);
        Assert.Equal(Album.Uri, list.Source);
        Assert.Equal(18, list.Tracks.Count);
        Assert.All(list.Tracks, t => Assert.Equal("Global Warming", t.Album));
        Assert.Equal(["Pitbull", "Chris Brown", "Jump Smokers"], list.Tracks[^1].Artists);
    }

    [Fact]
    public void A_list_that_isnt_public_is_unavailable()
    {
        Assert.Throws<SpotifyListUnavailableException>(() =>
            SpotifyLinkReader.ParseEmbed(Fixture("embed.not-found.html"), 200, TopHits));
    }

    [Theory]
    [InlineData("<html><body>Something else</body></html>")]
    [InlineData("<script id=\"__NEXT_DATA__\" type=\"application/json\">{not json</script>")]
    [InlineData("<script id=\"__NEXT_DATA__\" type=\"application/json\">{\"props\":{\"pageProps\":{}}}</script>")]
    public void A_page_that_isnt_an_embed_page_is_a_failed_request(string html)
    {
        Assert.Throws<HttpRequestException>(() => SpotifyLinkReader.ParseEmbed(html, 200, TopHits));
    }

    [Fact]
    public void Rows_that_arent_songs_are_skipped()
    {
        const string html = """
            <script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"state":{"data":{"entity":{
              "name":"Mixed","trackList":[
                {"uri":"spotify:episode:5Xt5DXGzch68nYYamXrNxZ","title":"An episode","subtitle":"A show","entityType":"episode"},
                {"uri":"spotify:track:7bxaFZ1O3cHkgLKMsdC3xR","title":"","subtitle":"Nobody","entityType":"track"},
                {"uri":"spotify:track:7bxaFZ1O3cHkgLKMsdC3xR","title":"Loser","subtitle":"Tame Impala","duration":0}
              ]}},"settings":{}}}}}</script>
            """;

        var list = SpotifyLinkReader.ParseEmbed(html, 200, TopHits).List;

        Assert.Equal(2, list.Skipped);
        var only = Assert.Single(list.Tracks);
        Assert.Null(only.Duration);
    }

    [Fact]
    public void A_page_of_the_web_players_api_lists_its_songs()
    {
        var page = SpotifyLinkReader.ParsePlaylistPage(Fixture("pathfinder.playlist-contents.synthetic.json"));

        Assert.Equal(104, page.Total);
        Assert.Equal(2, page.Skipped);
        Assert.Equal(2, page.Tracks.Count);
        var song = page.Tracks[1];
        Assert.Equal("Despacito", song.Title);
        Assert.Equal(["Luis Fonsi", "Daddy Yankee"], song.Artists);
        Assert.Equal("VIDA", song.Album);
        Assert.Equal(TimeSpan.FromMilliseconds(228826), song.Duration);
        Assert.Equal("6habFhsOp2NvshLv26DqMb", song.SpotifyId);
    }

    [Theory]
    [InlineData("""{"errors":[{"message":"PersistedQueryNotFound"}]}""", "PersistedQueryNotFound")]
    [InlineData("""{"data":{"playlistV2":{"__typename":"NotFound"}}}""", null)]
    [InlineData("<html>", null)]
    public void An_answer_without_songs_is_an_api_failure(string body, string? message)
    {
        var ex = Assert.Throws<SpotifyApiException>(() => SpotifyLinkReader.ParsePlaylistPage(body, 400));
        if (message is not null)
            Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void The_page_query_asks_for_the_playlists_rows_by_their_persisted_query()
    {
        using var doc = JsonDocument.Parse(SpotifyLinkReader.PageQuery(AllOut, 100, 100));
        var root = doc.RootElement;

        Assert.Equal("fetchPlaylistContents", root.GetProperty("operationName").GetString());
        Assert.Equal(AllOut.Uri, root.GetProperty("variables").GetProperty("uri").GetString());
        Assert.Equal(100, root.GetProperty("variables").GetProperty("offset").GetInt32());
        Assert.Equal(SpotifyLinkReader.PlaylistContentsHash,
            root.GetProperty("extensions").GetProperty("persistedQuery").GetProperty("sha256Hash").GetString());
    }

    [Fact]
    public async Task A_short_list_is_read_with_the_embed_page_alone()
    {
        var handler = new StubHandler(_ => Answer(Fixture("embed.playlist.html")));

        var list = await new SpotifyLinkReader(new HttpClient(handler)).ReadAsync(TopHits);

        Assert.Equal(50, list.Tracks.Count);
        var (request, _) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(TopHits.EmbedUrl, request.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_playlist_at_the_embed_pages_limit_reads_the_rest_with_the_pages_key()
    {
        var embed = Fixture("embed.playlist-100.html").Replace("\"accessToken\":\"REDACTED\"", "\"accessToken\":\"anon-key\"");
        var handler = new StubHandler(r => r.Method == HttpMethod.Get
            ? Answer(embed)
            : Answer(Fixture("pathfinder.playlist-contents.synthetic.json"), type: "application/json"));

        var list = await new SpotifyLinkReader(new HttpClient(handler)).ReadAsync(AllOut);

        Assert.False(list.Incomplete);
        Assert.Equal(102, list.Tracks.Count);
        Assert.Equal(2, list.Skipped);
        Assert.Equal("Blinding Lights", list.Tracks[100].Title);
        Assert.Equal(2, handler.Requests.Count);
        var (page, body) = handler.Requests[1];
        Assert.Equal(SpotifyLinkReader.PathfinderUri, page.RequestUri);
        Assert.Equal("Bearer anon-key", page.Headers.Authorization!.ToString());
        Assert.Contains("\"offset\":100", body);
    }

    [Fact]
    public async Task When_the_rest_cant_be_read_the_first_songs_are_kept_and_the_list_is_incomplete()
    {
        var embed = Fixture("embed.playlist-100.html").Replace("\"accessToken\":\"REDACTED\"", "\"accessToken\":\"anon-key\"");
        var handler = new StubHandler(r => r.Method == HttpMethod.Get
            ? Answer(embed)
            : Answer("""{"errors":[{"message":"PersistedQueryNotFound"}]}""", HttpStatusCode.BadRequest, "application/json"));

        var list = await new SpotifyLinkReader(new HttpClient(handler)).ReadAsync(AllOut);

        Assert.True(list.Incomplete);
        Assert.Equal(100, list.Tracks.Count);
    }

    [Fact]
    public async Task An_album_at_the_limit_is_incomplete_without_asking_further()
    {
        var full = Fixture("embed.playlist-100.html");
        var handler = new StubHandler(_ => Answer(full));

        var list = await new SpotifyLinkReader(new HttpClient(handler)).ReadAsync(Album);

        Assert.True(list.Incomplete);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_page_not_found_answer_is_unavailable()
    {
        var handler = new StubHandler(_ => Answer(Fixture("embed.not-found.html")));

        await Assert.ThrowsAsync<SpotifyListUnavailableException>(() =>
            new SpotifyLinkReader(new HttpClient(handler)).ReadAsync(TopHits));
    }
}
