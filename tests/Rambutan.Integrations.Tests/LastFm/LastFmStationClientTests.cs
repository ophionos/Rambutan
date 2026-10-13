using System.Net;
using System.Text;
using Rambutan.Core.Stations;
using Rambutan.Integrations.LastFm;

namespace Rambutan.Integrations.Tests.LastFm;

public class LastFmStationClientTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LastFm", name));

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return respond(request, ct);
        }
    }

    private static StubHandler Answer(HttpStatusCode status, string body, string type = "application/json") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, type),
        }));

    [Fact]
    public async Task A_recorded_station_reads_as_tracks_in_order()
    {
        var handler = Answer(HttpStatusCode.OK, Fixture("station.recommended.json"));
        var client = new LastFmStationClient(new HttpClient(handler));

        var tracks = await client.GetStationAsync("some user", StationKind.Recommended);

        Assert.Equal(
        [
            new StationTrack("Milburn", "Send in the Boys", TimeSpan.FromSeconds(166), "JvNTG7nphqg"),
            new StationTrack("Tiago Bettencourt", "Morena", TimeSpan.FromSeconds(238), "iI4JrOwrdOM"),
            new StationTrack("Ciclo Preparatório", "Lena Del Rey", null, "FsSS5r_zlnI"),
            new StationTrack("Toranja", "Carta", TimeSpan.FromSeconds(258), "Er5oxWm3oQA"),
        ], tracks);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://www.last.fm/player/station/user/some%20user/recommended", request.RequestUri!.AbsoluteUri);
        Assert.Contains(request.Headers.UserAgent, p => p.Product?.Name == "Rambutan");
    }

    [Theory]
    [InlineData(StationKind.Mix, "mix")]
    [InlineData(StationKind.Recommended, "recommended")]
    [InlineData(StationKind.Library, "library")]
    public void Each_kind_has_its_own_address(StationKind kind, string path) =>
        Assert.Equal($"https://www.last.fm/player/station/user/RJ/{path}", LastFmStationClient.StationUri("RJ", kind).AbsoluteUri);

    [Fact]
    public async Task A_recorded_artist_station_reads_as_tracks_in_order()
    {
        var handler = Answer(HttpStatusCode.OK, Fixture("station.artist-similar.json"));
        var client = new LastFmStationClient(new HttpClient(handler));

        var tracks = await client.GetArtistStationAsync("Daft Punk");

        Assert.Equal(
        [
            new StationTrack("Fred again..", "Places to Be", TimeSpan.FromSeconds(226), "0bVFEOb39vk"),
            new StationTrack("Daft Punk", "Harder, Better, Faster, Stronger", TimeSpan.FromSeconds(224), "gAjR4_CbPpQ"),
            new StationTrack("Mr. Oizo", "Positif", TimeSpan.FromSeconds(173), "p2OFUmv1X94"),
            new StationTrack("Justice vs. Simian", "We Are Your Friends", TimeSpan.FromSeconds(235), "L0TvnWRSyr4"),
        ], tracks);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://www.last.fm/player/station/music/Daft%20Punk/+similar", request.RequestUri!.AbsoluteUri);
    }

    // Each of these answered with a station when probed live (2026-10-13).
    [Theory]
    [InlineData("Florence + the Machine", "Florence%20%2B%20the%20Machine")]
    [InlineData("AC/DC", "AC%2FDC")]
    [InlineData("Björk", "Bj%C3%B6rk")]
    [InlineData("宇多田ヒカル", "%E5%AE%87%E5%A4%9A%E7%94%B0%E3%83%92%E3%82%AB%E3%83%AB")]
    public void An_artist_name_is_one_path_segment(string artist, string escaped) =>
        Assert.Equal($"https://www.last.fm/player/station/music/{escaped}/+similar",
            LastFmStationClient.ArtistStationUri(artist).AbsoluteUri);

    [Fact]
    public async Task An_unknown_artist_is_unavailable_not_a_network_failure()
    {
        // Last.fm answers an artist it doesn't know with HTTP 404 and its HTML 404 page.
        var client = new LastFmStationClient(new HttpClient(
            Answer(HttpStatusCode.NotFound, "<!doctype html><title>Page Not Found | Last.fm</title>", "text/html")));

        var ex = await Assert.ThrowsAsync<StationUnavailableException>(() =>
            client.GetArtistStationAsync("Zzznotanartist999"));
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public void Missing_links_durations_and_names_are_handled()
    {
        var tracks = LastFmStationClient.Parse(Fixture("station.edge-cases.synthetic.json"));

        Assert.Equal(
        [
            new StationTrack("Someone", "No Links", TimeSpan.FromSeconds(200), null),
            new StationTrack("Someone", "Spotify Only", null, null),
            new StationTrack("Fallback Artist", "Second Artist Named", null, "vid123"),
        ], tracks);
    }

    [Fact]
    public async Task An_unknown_user_is_unavailable_not_a_network_failure()
    {
        // Last.fm answers an unknown user with HTTP 500 and an HTML page (recorded).
        var client = new LastFmStationClient(new HttpClient(
            Answer(HttpStatusCode.InternalServerError, Fixture("station.unknown-user.html"), "text/html")));

        var ex = await Assert.ThrowsAsync<StationUnavailableException>(() =>
            client.GetStationAsync("this-user-does-not-exist", StationKind.Mix));
        Assert.Contains("500", ex.Message);
    }

    [Theory]
    [InlineData("""{"error": "nope"}""")]
    [InlineData("""{"playlist": null}""")]
    [InlineData("[]")]
    [InlineData("")]
    public void A_response_without_a_playlist_is_unavailable(string body) =>
        Assert.Throws<StationUnavailableException>(() => LastFmStationClient.Parse(body));

    [Fact]
    public void An_empty_playlist_is_an_empty_station() =>
        Assert.Empty(LastFmStationClient.Parse("""{"playlist": []}"""));

    [Fact]
    public async Task A_network_failure_stays_a_network_failure()
    {
        var client = new LastFmStationClient(new HttpClient(
            new StubHandler((_, _) => throw new HttpRequestException("connection refused"))));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStationAsync("RJ", StationKind.Mix));
    }

    [Fact]
    public async Task Cancelling_is_not_reported_as_a_network_failure()
    {
        using var cts = new CancellationTokenSource();
        var client = new LastFmStationClient(new HttpClient(new StubHandler(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        })));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetStationAsync("RJ", StationKind.Mix, cts.Token));
    }
}
