using System.Net;
using Rambutan.Integrations.ListenBrainz;
using Rambutan.Integrations.Scrobbling;
using Rambutan.Integrations.Tests.Scrobbling;

namespace Rambutan.Integrations.Tests.ListenBrainz;

public class ListenBrainzClientTests
{
    private const string Token = "abc-secret-token";

    private readonly FakeListenBrainzHandler _handler = new();
    private readonly Uri _server = new("https://lb.example/apis/listenbrainz/");
    private readonly ListenBrainzClient _client;

    public ListenBrainzClientTests() =>
        _client = new ListenBrainzClient(_server, "1.2.3", new HttpClient(_handler));

    private static ScrobbleEntry Entry(string track = "Instant Crush", long timestamp = 1443521965) => new()
    {
        Artist = "Daft Punk",
        Track = track,
        Album = "Random Access Memories",
        DurationSeconds = 222,
        Timestamp = timestamp,
    };

    [Theory]
    [InlineData("https://api.listenbrainz.org", "https://api.listenbrainz.org/")]
    [InlineData("  https://api.listenbrainz.org/  ", "https://api.listenbrainz.org/")]
    [InlineData("http://192.168.1.5:42010/apis/listenbrainz", "http://192.168.1.5:42010/apis/listenbrainz/")]
    public void TryParseServer_AcceptsHttpAndHttps_AndEndsThePathWithASlash(string text, string expected)
    {
        Assert.True(ListenBrainzClient.TryParseServer(text, out var server));
        Assert.Equal(expected, server!.AbsoluteUri);
    }

    [Fact]
    public void TryParseServer_Result_ResolvesTheApiPathsUnderItsOwnPath()
    {
        ListenBrainzClient.TryParseServer("http://192.168.1.5:42010/apis/listenbrainz", out var server);

        Assert.Equal("http://192.168.1.5:42010/apis/listenbrainz/1/submit-listens", new Uri(server!, "1/submit-listens").AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("listenbrainz.org")]
    [InlineData("/apis/listenbrainz")]
    [InlineData("ftp://lb.example/")]
    [InlineData("https://user:pass@lb.example/")]
    [InlineData("https://lb.example/?token=1")]
    [InlineData("https://lb.example/#top")]
    public void TryParseServer_RejectsEverythingElse(string? text)
    {
        Assert.False(ListenBrainzClient.TryParseServer(text, out var server));
        Assert.Null(server);
    }

    [Fact]
    public void IsUnencrypted_IsTrueForHttpOnly()
    {
        Assert.True(ListenBrainzClient.IsUnencrypted(new Uri("http://lb.example/")));
        Assert.False(ListenBrainzClient.IsUnencrypted(new Uri("https://lb.example/")));
    }

    [Fact]
    public async Task ValidateToken_Valid_ReturnsTheUserName_ThroughTheServersPath()
    {
        var result = await _client.ValidateTokenAsync(Token);

        Assert.True(result.IsOk);
        Assert.Equal("demo-listener", result.Value);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://lb.example/apis/listenbrainz/1/validate-token", request.Uri.AbsoluteUri);
        Assert.Equal($"Token {Token}", request.Authorization);
    }

    [Fact]
    public async Task ValidateToken_ValidFalse_IsInvalidToken()
    {
        _handler.Enqueue("validate-token", HttpStatusCode.OK, """{"code":200,"message":"Token invalid.","valid":false}""");

        Assert.Equal(ListenBrainzOutcome.InvalidToken, (await _client.ValidateTokenAsync(Token)).Outcome);
    }

    [Fact]
    public async Task ValidateToken_401_IsInvalidToken()
    {
        _handler.EnqueueError("validate-token", HttpStatusCode.Unauthorized);

        Assert.Equal(ListenBrainzOutcome.InvalidToken, (await _client.ValidateTokenAsync(Token)).Outcome);
    }

    [Fact]
    public async Task ValidateToken_AnHtmlPage_IsNotAServer()
    {
        _handler.Enqueue("validate-token", HttpStatusCode.OK, "<html><body>Welcome</body></html>", "text/html");

        Assert.Equal(ListenBrainzOutcome.NotAServer, (await _client.ValidateTokenAsync(Token)).Outcome);
    }

    [Fact]
    public async Task ValidateToken_JsonOfAnotherShape_IsNotAServer()
    {
        _handler.Enqueue("validate-token", HttpStatusCode.OK, """{"hello":"world"}""");

        Assert.Equal(ListenBrainzOutcome.NotAServer, (await _client.ValidateTokenAsync(Token)).Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ListenBrainzOutcome.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ListenBrainzOutcome.RetryLater)]
    [InlineData(HttpStatusCode.NotFound, ListenBrainzOutcome.Rejected)]
    public async Task ValidateToken_OtherStatuses_MapToTheirOutcome(HttpStatusCode status, ListenBrainzOutcome expected)
    {
        _handler.EnqueueError("validate-token", status);

        Assert.Equal(expected, (await _client.ValidateTokenAsync(Token)).Outcome);
    }

    [Fact]
    public async Task ValidateToken_NetworkFailure_IsRetryLater_NotAnException()
    {
        _handler.EnqueueNetworkFailure("validate-token");

        Assert.Equal(ListenBrainzOutcome.RetryLater, (await _client.ValidateTokenAsync(Token)).Outcome);
    }

    [Fact]
    public async Task Submit_Single_SendsExactlyTheDisclosedFields()
    {
        var result = await _client.SubmitAsync(Token, ListenType.Single, [Entry()]);

        Assert.True(result.IsOk);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://lb.example/apis/listenbrainz/1/submit-listens", request.Uri.AbsoluteUri);
        Assert.Equal($"Token {Token}", request.Authorization);
        Assert.Equal(
            """{"listen_type":"single","payload":[{"listened_at":1443521965,"track_metadata":{"artist_name":"Daft Punk","track_name":"Instant Crush","release_name":"Random Access Memories","additional_info":{"media_player":"Rambutan","submission_client":"Rambutan","submission_client_version":"1.2.3","duration_ms":222000}}}]}""",
            request.Body);
    }

    [Fact]
    public async Task Submit_PlayingNow_HasNoListenedAt()
    {
        await _client.SubmitAsync(Token, ListenType.PlayingNow, [Entry()]);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("playing_now", request.ListenType);
        Assert.False(request.Payload.Single().TryGetProperty("listened_at", out _));
    }

    [Fact]
    public async Task Submit_NullAlbumAndDuration_AreOmitted()
    {
        await _client.SubmitAsync(Token, ListenType.Single, [Entry() with { Album = null, DurationSeconds = null }]);

        var metadata = _handler.Requests.Single().Payload.Single().GetProperty("track_metadata");
        Assert.False(metadata.TryGetProperty("release_name", out _));
        Assert.False(metadata.GetProperty("additional_info").TryGetProperty("duration_ms", out _));
    }

    [Fact]
    public async Task Submit_Import_SendsEveryEntryInOrder()
    {
        await _client.SubmitAsync(Token, ListenType.Import, [Entry("A", 1), Entry("B", 2), Entry("C", 3)]);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("import", request.ListenType);
        Assert.Equal(["A", "B", "C"], request.Tracks);
        Assert.Equal([1L, 2L, 3L], request.Payload.Select(p => p.GetProperty("listened_at").GetInt64()));
    }

    [Fact]
    public async Task Submit_400_IsRejected_AndCarriesTheServersError()
    {
        _handler.EnqueueError("submit-listens", HttpStatusCode.BadRequest, "listened_at is too old");

        var result = await _client.SubmitAsync(Token, ListenType.Single, [Entry()]);

        Assert.Equal(ListenBrainzOutcome.Rejected, result.Outcome);
        Assert.Contains("listened_at is too old", result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ListenBrainzOutcome.InvalidToken)]
    [InlineData(HttpStatusCode.TooManyRequests, ListenBrainzOutcome.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, ListenBrainzOutcome.RetryLater)]
    [InlineData(HttpStatusCode.Forbidden, ListenBrainzOutcome.Rejected)]
    public async Task Submit_OtherStatuses_MapToTheirOutcome(HttpStatusCode status, ListenBrainzOutcome expected)
    {
        _handler.EnqueueError("submit-listens", status);

        Assert.Equal(expected, (await _client.SubmitAsync(Token, ListenType.Single, [Entry()])).Outcome);
    }

    [Fact]
    public async Task Submit_NetworkFailure_IsRetryLater()
    {
        _handler.EnqueueNetworkFailure("submit-listens");

        Assert.Equal(ListenBrainzOutcome.RetryLater, (await _client.SubmitAsync(Token, ListenType.Single, [Entry()])).Outcome);
    }

    [Fact]
    public async Task Submit_CountsOutsideTheTypesRange_Throw()
    {
        var tooMany = Enumerable.Range(0, ListenBrainzClient.MaxBatch + 1).Select(i => Entry($"T{i}", i)).ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() => _client.SubmitAsync(Token, ListenType.Single, []));
        await Assert.ThrowsAsync<ArgumentException>(() => _client.SubmitAsync(Token, ListenType.Single, [Entry(), Entry()]));
        await Assert.ThrowsAsync<ArgumentException>(() => _client.SubmitAsync(Token, ListenType.PlayingNow, [Entry(), Entry()]));
        await Assert.ThrowsAsync<ArgumentException>(() => _client.SubmitAsync(Token, ListenType.Import, []));
        await Assert.ThrowsAsync<ArgumentException>(() => _client.SubmitAsync(Token, ListenType.Import, tooMany));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task TheToken_NeverAppearsInAResultsText()
    {
        var results = new List<string>();
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError })
        {
            _handler.EnqueueError("validate-token", status);
            _handler.EnqueueError("submit-listens", status);
            results.Add((await _client.ValidateTokenAsync(Token)).ToString());
            results.Add((await _client.SubmitAsync(Token, ListenType.Single, [Entry()])).ToString());
        }
        _handler.EnqueueNetworkFailure("validate-token");
        _handler.EnqueueNetworkFailure("submit-listens");
        results.Add((await _client.ValidateTokenAsync(Token)).ToString());
        results.Add((await _client.SubmitAsync(Token, ListenType.Single, [Entry()])).ToString());

        Assert.All(results, text => Assert.DoesNotContain(Token, text));
    }
}
