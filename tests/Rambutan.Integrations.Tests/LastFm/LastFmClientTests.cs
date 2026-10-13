using System.Net;
using Rambutan.Integrations.LastFm;

namespace Rambutan.Integrations.Tests.LastFm;

public class LastFmClientTests
{
    private const string Key = "k123";
    private const string Secret = "s3cret";

    private readonly FakeLastFmHandler _handler = new();
    private readonly LastFmClient _client;

    public LastFmClientTests() => _client = new LastFmClient(Key, Secret, new HttpClient(_handler));

    [Fact]
    public async Task EveryCall_IsASignedJsonPost_ToTheApiRoot()
    {
        await _client.GetTokenAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(LastFmClient.Endpoint, request.Uri);
        Assert.Equal("json", request.Form["format"]);
        Assert.Equal(Key, request.Form["api_key"]);
        var unsigned = request.Form.Where(p => p.Key != "api_sig");
        Assert.Equal(LastFmSignature.Sign(unsigned, Secret), request.Form["api_sig"]);
    }

    [Fact]
    public async Task AuthFlow_TokenThenSession()
    {
        var token = await _client.GetTokenAsync();
        Assert.True(token.IsOk);
        Assert.Equal("cf45fe5a3e3cebe168480a086d7fe481", token.Value);
        Assert.Equal(
            "https://www.last.fm/api/auth/?api_key=k123&token=cf45fe5a3e3cebe168480a086d7fe481",
            _client.AuthorizeUrl(token.Value!));

        var session = await _client.GetSessionAsync(token.Value!);

        Assert.True(session.IsOk);
        Assert.Equal(new LastFmSession("DemoListener", "d580d57f32848f5dcf574d1ce18d78b2"), session.Value);
        Assert.Equal(token.Value, _handler.Calls("auth.getSession").Single().Form["token"]);
    }

    [Theory]
    [InlineData(14, LastFmOutcome.NotApproved)]
    [InlineData(15, LastFmOutcome.TokenExpired)]
    [InlineData(4, LastFmOutcome.TokenExpired)]
    [InlineData(11, LastFmOutcome.RetryLater)]
    public async Task GetSession_ExplainsWhyNot(int code, LastFmOutcome expected)
    {
        _handler.EnqueueError("auth.getSession", code, HttpStatusCode.Forbidden);

        var session = await _client.GetSessionAsync("t");

        Assert.Equal(expected, session.Outcome);
        Assert.Equal(code, session.ErrorCode);
    }

    [Fact]
    public async Task NowPlaying_SendsTrackFields_AndTheSession()
    {
        var result = await _client.UpdateNowPlayingAsync("SK",
            new ScrobbleEntry { Artist = "Daft Punk", Track = "Instant Crush", Album = "RAM", DurationSeconds = 337 });

        Assert.True(result.IsOk);
        var form = _handler.Calls("track.updateNowPlaying").Single().Form;
        Assert.Equal("SK", form["sk"]);
        Assert.Equal("Daft Punk", form["artist"]);
        Assert.Equal("Instant Crush", form["track"]);
        Assert.Equal("RAM", form["album"]);
        Assert.Equal("337", form["duration"]);
        Assert.False(form.ContainsKey("timestamp"));
    }

    [Fact]
    public async Task Scrobble_IndexesEachPlay_AndReadsTheVerdict()
    {
        var result = await _client.ScrobbleAsync("SK",
        [
            Tracks.Entry("One More Time", 1700000000) with { Album = "Discovery" },
            Tracks.Entry("D.A.N.C.E.", 1700000300, artist: "Justice"),
        ]);

        Assert.True(result.IsOk);
        Assert.Equal(new ScrobbleBatchResult(Accepted: 1, Ignored: 1), result.Value);
        var form = _handler.Calls("track.scrobble").Single().Form;
        Assert.Equal("One More Time", form["track[0]"]);
        Assert.Equal("Discovery", form["album[0]"]);
        Assert.Equal("1700000000", form["timestamp[0]"]);
        Assert.Equal("Justice", form["artist[1]"]);
        Assert.Equal("1700000300", form["timestamp[1]"]);
        Assert.False(form.ContainsKey("album[1]"));
    }

    [Fact]
    public async Task Scrobble_ReadsASingleResult_WhichLastFmSendsAsAnObject()
    {
        var result = await _client.ScrobbleAsync("SK", [Tracks.Entry("Instant Crush", 1700000000)]);

        Assert.Equal(new ScrobbleBatchResult(1, 0), result.Value);
    }

    [Fact]
    public async Task Scrobble_RefusesEmptyOrOversizedBatches()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.ScrobbleAsync("SK", []));
        var tooMany = Enumerable.Range(0, LastFmClient.MaxBatch + 1).Select(i => Tracks.Entry("T", i)).ToArray();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.ScrobbleAsync("SK", tooMany));
    }

    [Theory]
    [InlineData(9, LastFmOutcome.InvalidSession)]
    [InlineData(11, LastFmOutcome.RetryLater)]
    [InlineData(16, LastFmOutcome.RetryLater)]
    [InlineData(8, LastFmOutcome.RetryLater)]
    [InlineData(29, LastFmOutcome.RateLimited)]
    [InlineData(10, LastFmOutcome.ClientMisconfigured)]
    [InlineData(13, LastFmOutcome.ClientMisconfigured)]
    [InlineData(26, LastFmOutcome.ClientMisconfigured)]
    [InlineData(6, LastFmOutcome.Rejected)]
    [InlineData(7, LastFmOutcome.Rejected)]
    public async Task ErrorCodes_MapToWhatToDo(int code, LastFmOutcome expected)
    {
        _handler.EnqueueError("track.scrobble", code);

        var result = await _client.ScrobbleAsync("SK", [Tracks.Entry("T", 1)]);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public async Task AnErrorBody_CountsEvenWithA200()
    {
        _handler.EnqueueError("track.scrobble", 9, HttpStatusCode.OK);

        var result = await _client.ScrobbleAsync("SK", [Tracks.Entry("T", 1)]);

        Assert.Equal(LastFmOutcome.InvalidSession, result.Outcome);
    }

    [Fact]
    public async Task NetworkFailure_IsRetryLater()
    {
        _handler.EnqueueNetworkFailure("track.scrobble");

        var result = await _client.ScrobbleAsync("SK", [Tracks.Entry("T", 1)]);

        Assert.Equal(LastFmOutcome.RetryLater, result.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task UnreadableResponses_AreRetryLater(HttpStatusCode status, string body)
    {
        _handler.Enqueue("track.scrobble", status, body);

        var result = await _client.ScrobbleAsync("SK", [Tracks.Entry("T", 1)]);

        Assert.Equal(LastFmOutcome.RetryLater, result.Outcome);
    }

    [Fact]
    public async Task OkWithoutTheExpectedFields_IsRetryLater()
    {
        _handler.Enqueue("auth.getToken", HttpStatusCode.OK, "{}");

        var token = await _client.GetTokenAsync();

        Assert.Equal(LastFmOutcome.RetryLater, token.Outcome);
    }
}
