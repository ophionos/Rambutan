using System.Net;
using System.Text;
using System.Text.Json;
using Rambutan.Core.Stations;
using Rambutan.Integrations.ListenBrainz;

namespace Rambutan.Integrations.Tests.ListenBrainz;

public class ListenBrainzCreatedForTests
{
    private const string DailyId = "e7ee1f31-9564-51a4-983e-89fd927868e2";

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ListenBrainz", name));

    private sealed class Handler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

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

    private static (Handler Handler, ListenBrainzRadioClient Client) Make(
        Func<HttpRequestMessage, (HttpStatusCode, string)> respond, string? token = "tok")
    {
        var handler = new Handler(respond);
        return (handler, new ListenBrainzRadioClient(() => token, new HttpClient(handler)));
    }

    private static (HttpStatusCode, string) Ok(string body) => (HttpStatusCode.OK, body);

    private static string One(string date, string? expires) =>
        "{\"playlists\":[{\"playlist\":{\"identifier\":\"https://listenbrainz.org/playlist/abc\","
        + (date is "" ? "" : $"\"date\":\"{date}\",")
        + "\"extension\":{\"https://musicbrainz.org/doc/jspf#playlist\":{\"additional_metadata\":{"
        + (expires is null ? "" : $"\"expires_at\":\"{expires}\",")
        + "\"algorithm_metadata\":{\"source_patch\":\"daily-jams\"}}}}}}]}";

    [Fact]
    public void A_recorded_list_reads_the_daily_and_weekly_playlists_only()
    {
        var listed = ListenBrainzRadioClient.ParseCreatedFor(Fixture("createdfor.json"));

        using var doc = JsonDocument.Parse(Fixture("createdfor.json"));
        var expected = doc.RootElement.GetProperty("playlists").EnumerateArray().Count(p =>
            p.GetProperty("playlist").GetProperty("extension")
                .GetProperty("https://musicbrainz.org/doc/jspf#playlist")
                .GetProperty("additional_metadata").GetProperty("algorithm_metadata")
                .GetProperty("source_patch").GetString() is "daily-jams" or "weekly-jams" or "weekly-exploration");

        Assert.Equal(expected, listed.Count);
        Assert.Contains(CreatedForKind.WeeklyJams, listed.Select(p => p.Kind));
        Assert.Contains(CreatedForKind.WeeklyExploration, listed.Select(p => p.Kind));
        var daily = Assert.Single(listed, p => p.Kind == CreatedForKind.DailyJams);
        Assert.Equal(DailyId, daily.Id);
        Assert.Equal(TimeSpan.Zero, daily.ExpiresAt!.Value.Offset);
        Assert.Equal(new DateTime(2026, 12, 4, 1, 10, 53, DateTimeKind.Utc).AddTicks(1091850), daily.ExpiresAt.Value.UtcDateTime);
    }

    [Fact]
    public void Only_the_daily_jams_is_current_after_the_weekly_ones_expired()
    {
        var listed = ListenBrainzRadioClient.ParseCreatedFor(Fixture("createdfor.json"));

        var current = CreatedFor.Current(listed, new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(DailyId, Assert.Single(current).Id);
    }

    [Fact]
    public void A_user_with_none_has_an_empty_list() =>
        Assert.Empty(ListenBrainzRadioClient.ParseCreatedFor(Fixture("createdfor.none.json")));

    [Theory]
    [InlineData("<html>")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void An_answer_without_a_list_is_unavailable(string body) =>
        Assert.Throws<StationUnavailableException>(() => ListenBrainzRadioClient.ParseCreatedFor(body));

    [Fact]
    public void An_expiry_without_an_offset_reads_as_utc()
    {
        var p = Assert.Single(ListenBrainzRadioClient.ParseCreatedFor(One("2026-11-20T01:00:00+00:00", "2026-12-04T01:10:53.5")));

        Assert.Equal(TimeSpan.Zero, p.ExpiresAt!.Value.Offset);
        Assert.Equal(new DateTime(2026, 12, 4, 1, 10, 53, 500, DateTimeKind.Utc), p.ExpiresAt.Value.UtcDateTime);
    }

    [Fact]
    public void A_missing_or_unreadable_expiry_is_null()
    {
        Assert.Null(Assert.Single(ListenBrainzRadioClient.ParseCreatedFor(One("2026-11-20T01:00:00+00:00", null))).ExpiresAt);
        Assert.Null(Assert.Single(ListenBrainzRadioClient.ParseCreatedFor(One("2026-11-20T01:00:00+00:00", "soon"))).ExpiresAt);
    }

    [Fact]
    public void A_playlist_without_a_date_is_skipped() =>
        Assert.Empty(ListenBrainzRadioClient.ParseCreatedFor(One("", "2026-12-04T01:10:53")));

    [Fact]
    public void A_recorded_daily_playlist_reads_as_tracks()
    {
        var tracks = ListenBrainzRadioClient.ParsePlaylist(Fixture("playlist.daily-jams.json"));

        Assert.Equal(50, tracks.Count);
        Assert.Equal("Elder", tracks[0].Artist);
        Assert.Equal("Capture/Release", tracks[0].Title);
        Assert.Equal(TimeSpan.FromMilliseconds(527533), tracks[0].Duration);
    }

    [Theory]
    [InlineData("playlist.weekly-jams.json")]
    [InlineData("playlist.weekly-exploration.json")]
    public void The_other_recorded_playlists_have_tracks(string fixture) =>
        Assert.NotEmpty(ListenBrainzRadioClient.ParsePlaylist(Fixture(fixture)));

    [Theory]
    [InlineData("<html>")]
    [InlineData("""{"playlist":{}}""")]
    [InlineData("{}")]
    public void An_answer_without_a_playlist_is_unavailable(string body) =>
        Assert.Throws<StationUnavailableException>(() => ListenBrainzRadioClient.ParsePlaylist(body));

    [Fact]
    public async Task An_unknown_user_is_unavailable()
    {
        var (_, client) = Make(_ => (HttpStatusCode.NotFound, Fixture("createdfor.unknown-user.json")));

        var ex = await Assert.ThrowsAsync<StationUnavailableException>(() => client.GetCreatedForAsync("nobody"));

        Assert.Contains("Cannot find user", ex.Message);
    }

    [Fact]
    public async Task An_unknown_playlist_is_unavailable()
    {
        var (_, client) = Make(_ => (HttpStatusCode.NotFound, Fixture("playlist.not-found.json")));

        await Assert.ThrowsAsync<StationUnavailableException>(() => client.GetPlaylistAsync("gone"));
    }

    [Fact]
    public async Task A_server_fault_is_a_network_failure()
    {
        var (_, client) = Make(_ => (HttpStatusCode.ServiceUnavailable, "{}"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetCreatedForAsync("u"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetPlaylistAsync("p"));
    }

    [Fact]
    public async Task These_requests_carry_no_token_but_the_radio_still_does()
    {
        var (handler, client) = Make(r => Ok(
            r.RequestUri!.AbsolutePath.Contains("createdfor") ? Fixture("createdfor.json")
            : r.RequestUri.AbsolutePath.Contains("playlist/") ? Fixture("playlist.daily-jams.json")
            : r.RequestUri.AbsolutePath.Contains("metadata/lookup") ? "{}"
            : Fixture("lb-radio.artist-name.json")), "secret");

        await client.GetCreatedForAsync("example-user");
        await client.GetPlaylistAsync(DailyId);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Null(r.Headers.Authorization);
            Assert.Contains(r.Headers.UserAgent, p => p.Product?.Name == "Rambutan");
        });

        await client.GetArtistStationAsync(new ArtistSeed("Radiohead", []));

        Assert.Equal("Token secret", handler.Requests[^1].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task A_user_name_is_escaped_in_the_path()
    {
        var (handler, client) = Make(_ => Ok(Fixture("createdfor.none.json")));

        await client.GetCreatedForAsync("a b/c");

        Assert.Equal(
            "https://api.listenbrainz.org/1/user/a%20b%2Fc/playlists/createdfor?count=100",
            Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
        Assert.Equal(
            "https://api.listenbrainz.org/1/playlist/x%2Fy",
            ListenBrainzRadioClient.PlaylistUri("x/y").AbsoluteUri);
    }
}
