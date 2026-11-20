using System.Net;
using System.Text;
using Rambutan.Core.Stations;
using Rambutan.Integrations.ListenBrainz;

namespace Rambutan.Integrations.Tests.ListenBrainz;

public class ListenBrainzRadioClientTests
{
    private const string Mbid = "b5e11a13-9358-4fbf-8eb6-a90e55c520ab";

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ListenBrainz", name));

    private sealed class Handler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public IEnumerable<HttpRequestMessage> Lookups => Requests.Where(r => r.RequestUri!.AbsolutePath.Contains("metadata/lookup"));

        public IEnumerable<HttpRequestMessage> Radios => Requests.Where(r => r.RequestUri!.AbsolutePath.Contains("lb-radio"));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var (status, body) = respond(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static bool IsLookup(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.Contains("metadata/lookup");

    private static (Handler Handler, ListenBrainzRadioClient Client) Make(
        Func<HttpRequestMessage, (HttpStatusCode, string)> respond, string? token = "tok")
    {
        var handler = new Handler(respond);
        return (handler, new ListenBrainzRadioClient(() => token, new HttpClient(handler)));
    }

    private static (HttpStatusCode, string) Ok(string body) => (HttpStatusCode.OK, body);

    [Fact]
    public void A_recorded_radio_by_id_reads_as_tracks_in_order()
    {
        var tracks = ListenBrainzRadioClient.ParseRadio(Fixture("lb-radio.artist-mbid.json"));

        Assert.Equal(50, tracks.Count);
        Assert.Equal("Capotes Pretos, Capotes Brancos", tracks[0].Title);
        Assert.Null(tracks[0].Duration);
        Assert.All(tracks, t => Assert.Null(t.VideoId));
    }

    [Fact]
    public void A_recorded_radio_by_name_reads_the_credit_and_the_duration_in_milliseconds()
    {
        var tracks = ListenBrainzRadioClient.ParseRadio(Fixture("lb-radio.artist-name.json"));

        Assert.Equal(47, tracks.Count);
        Assert.Equal("Queen + Paul Rodgers", tracks[0].Artist);
        Assert.Equal("Intro / Hammer to Fall (London 13 October 2008)", tracks[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(120), tracks[0].Duration);
        Assert.Null(tracks[16].Duration);
    }

    [Fact]
    public void A_track_without_a_title_or_credit_is_skipped_and_text_is_trimmed()
    {
        var tracks = ListenBrainzRadioClient.ParseRadio("""
            {"payload":{"jspf":{"playlist":{"track":[
              {"title":"  A  ","creator":" X ","duration":0},
              {"title":" ","creator":"Y"},
              {"title":"B"},
              {"creator":"Z"},
              {"title":"C","creator":"W","duration":1500}]}}}}
            """);

        Assert.Equal([new StationTrack("X", "A"), new StationTrack("W", "C", TimeSpan.FromMilliseconds(1500))], tracks);
    }

    [Theory]
    [InlineData("""{"payload":{}}""")]
    [InlineData("""{"payload":{"jspf":{"playlist":{}}}}""")]
    [InlineData("<html>")]
    [InlineData("[]")]
    public void An_answer_without_a_playlist_is_unavailable(string body) =>
        Assert.Throws<StationUnavailableException>(() => ListenBrainzRadioClient.ParseRadio(body));

    [Fact]
    public async Task A_lookup_hit_asks_the_radio_by_the_artists_id()
    {
        var (handler, client) = Make(r => Ok(IsLookup(r) ? Fixture("metadata-lookup.json") : Fixture("lb-radio.artist-mbid.json")));

        var tracks = await client.GetArtistStationAsync(new ArtistSeed("Fausto", ["Por este rio acima", "Other"]));

        Assert.Equal(50, tracks.Count);
        Assert.Single(handler.Lookups);
        Assert.Equal(ListenBrainzRadioClient.LookupUri("Fausto", "Por este rio acima"), handler.Lookups.Single().RequestUri);
        var radio = Assert.Single(handler.Radios);
        Assert.Equal(ListenBrainzRadioClient.RadioUri($"artist:({Mbid})"), radio.RequestUri);
        Assert.EndsWith("mode=easy", radio.RequestUri!.Query);
    }

    [Fact]
    public async Task Lookups_that_all_miss_ask_the_radio_by_name()
    {
        var (handler, client) = Make(r => Ok(IsLookup(r) ? Fixture("metadata-lookup.miss.json") : Fixture("lb-radio.artist-name.json")));

        await client.GetArtistStationAsync(new ArtistSeed("Radiohead", ["Karma Police", "Creep"]));

        Assert.Equal(2, handler.Lookups.Count());
        Assert.Equal(ListenBrainzRadioClient.RadioUri("artist:(Radiohead)"), Assert.Single(handler.Radios).RequestUri);
    }

    [Fact]
    public async Task A_hit_for_another_artists_credit_is_rejected()
    {
        var (handler, client) = Make(r => Ok(IsLookup(r)
            ? $$"""{"artist_credit_name":"Faustones","artist_mbids":["{{Mbid}}"]}"""
            : Fixture("lb-radio.artist-name.json")));

        await client.GetArtistStationAsync(new ArtistSeed("Fausto", ["A"]));

        Assert.Equal(ListenBrainzRadioClient.RadioUri("artist:(Fausto)"), Assert.Single(handler.Radios).RequestUri);
    }

    [Fact]
    public async Task At_most_three_distinct_titles_are_looked_up()
    {
        var (handler, client) = Make(r => Ok(IsLookup(r) ? "{}" : Fixture("lb-radio.artist-name.json")));

        await client.GetArtistStationAsync(new ArtistSeed("Fausto", ["A", "A", " ", "B", "C", "D", "E"]));

        Assert.Equal(["A", "B", "C"], handler.Lookups.Select(r => Query(r, "recording_name")));
    }

    [Fact]
    public async Task Lookups_stop_at_the_first_hit()
    {
        var calls = 0;
        var (handler, client) = Make(r => Ok(IsLookup(r)
            ? ++calls == 2 ? Fixture("metadata-lookup.json") : "{}"
            : Fixture("lb-radio.artist-mbid.json")));

        await client.GetArtistStationAsync(new ArtistSeed("Fausto", ["A", "B", "C"]));

        Assert.Equal(2, handler.Lookups.Count());
        Assert.Contains(Mbid, Uri.UnescapeDataString(handler.Radios.Single().RequestUri!.Query));
    }

    [Fact]
    public async Task With_no_song_titles_nothing_is_looked_up()
    {
        var (handler, client) = Make(_ => Ok(Fixture("lb-radio.artist-name.json")));

        await client.GetArtistStationAsync(new ArtistSeed("Radiohead", []));

        Assert.Empty(handler.Lookups);
        Assert.Single(handler.Radios);
    }

    [Fact]
    public async Task An_artist_the_radio_cannot_look_up_is_unavailable_with_the_servers_reason()
    {
        var (_, client) = Make(_ => (HttpStatusCode.BadRequest, Fixture("lb-radio.artist-unknown.json")));

        var ex = await Assert.ThrowsAsync<StationUnavailableException>(
            () => client.GetArtistStationAsync(new ArtistSeed("Fausto Bordalo Dias", [])));

        Assert.IsNotType<StationTokenRejectedException>(ex);
        Assert.Contains("could not be looked up", ex.Message);
    }

    [Fact]
    public async Task A_refused_token_on_the_radio_is_reported_as_such()
    {
        var (_, client) = Make(_ => (HttpStatusCode.Unauthorized, Fixture("lb-radio.no-token.json")));

        await Assert.ThrowsAsync<StationTokenRejectedException>(
            () => client.GetArtistStationAsync(new ArtistSeed("Radiohead", [])));
    }

    [Fact]
    public async Task A_refused_token_on_the_lookup_is_reported_as_such()
    {
        var (handler, client) = Make(_ => (HttpStatusCode.Unauthorized, Fixture("lb-radio.no-token.json")));

        await Assert.ThrowsAsync<StationTokenRejectedException>(
            () => client.GetArtistStationAsync(new ArtistSeed("Radiohead", ["Karma Police"])));

        Assert.Empty(handler.Radios);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_rate_limit_or_a_server_fault_is_a_network_failure(HttpStatusCode status)
    {
        var (_, client) = Make(_ => (status, "{}"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetArtistStationAsync(new ArtistSeed("Radiohead", [])));
    }

    [Fact]
    public async Task A_200_that_is_not_a_station_is_unavailable()
    {
        var (_, client) = Make(_ => Ok("<html>"));

        await Assert.ThrowsAsync<StationUnavailableException>(
            () => client.GetArtistStationAsync(new ArtistSeed("Radiohead", [])));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Without_a_token_nothing_is_sent(string? token)
    {
        var (handler, client) = Make(_ => Ok(Fixture("lb-radio.artist-name.json")), token);

        await Assert.ThrowsAsync<StationTokenRejectedException>(
            () => client.GetArtistStationAsync(new ArtistSeed("Radiohead", ["Karma Police"])));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Every_request_carries_the_token_and_the_user_agent()
    {
        var (handler, client) = Make(r => Ok(IsLookup(r) ? "{}" : Fixture("lb-radio.artist-name.json")), "secret");

        await client.GetArtistStationAsync(new ArtistSeed("Radiohead", ["Karma Police"]));

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("Token secret", r.Headers.Authorization!.ToString());
            Assert.Contains(r.Headers.UserAgent, p => p.Product?.Name == "Rambutan");
            Assert.Equal("api.listenbrainz.org", r.RequestUri!.Host);
        });
    }

    [Fact]
    public async Task A_name_with_parentheses_and_no_id_is_not_sent_to_the_radio()
    {
        var (handler, client) = Make(_ => Ok("{}"));

        await Assert.ThrowsAsync<StationUnavailableException>(
            () => client.GetArtistStationAsync(new ArtistSeed("Foo (band)", ["A"])));

        Assert.Empty(handler.Radios);
    }

    [Fact]
    public async Task A_name_with_parentheses_is_fine_when_a_lookup_finds_its_id()
    {
        var (handler, client) = Make(r => Ok(IsLookup(r)
            ? $$"""{"artist_credit_name":"Foo (band)","artist_mbids":["{{Mbid}}"]}"""
            : Fixture("lb-radio.artist-mbid.json")));

        await client.GetArtistStationAsync(new ArtistSeed("Foo (band)", ["A"]));

        Assert.Equal(ListenBrainzRadioClient.RadioUri($"artist:({Mbid})"), Assert.Single(handler.Radios).RequestUri);
    }

    [Fact]
    public void Query_values_are_escaped()
    {
        Assert.Equal(
            "https://api.listenbrainz.org/1/explore/lb-radio?prompt=artist%3A%28a%20%26%20b%29&mode=easy",
            ListenBrainzRadioClient.RadioUri("artist:(a & b)").AbsoluteUri);
        Assert.Equal(
            "https://api.listenbrainz.org/1/metadata/lookup/?artist_name=a%20%26%20b&recording_name=x%3Fy",
            ListenBrainzRadioClient.LookupUri("a & b", "x?y").AbsoluteUri);
    }

    private static string Query(HttpRequestMessage request, string key) =>
        request.RequestUri!.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .Where(p => p[0] == key)
            .Select(p => Uri.UnescapeDataString(p[1]))
            .Single();
}
