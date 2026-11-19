using System.Net;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Integrations.ListenBrainz;
using Rambutan.Integrations.Scrobbling;
using Rambutan.Integrations.Tests.Scrobbling;

namespace Rambutan.Integrations.Tests.ListenBrainz;

public sealed class ListenBrainzScrobblerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 20, 0, 0, TimeSpan.Zero);
    private static readonly Uri Home = new("https://lb.example/");
    private static readonly ListenBrainzConnection Saved = new(Home, "demo-listener", "TOKEN");

    private readonly TempDir _dir = new();
    private readonly ManualClock _clock = new(T0);
    private readonly FakeListenBrainzHandler _handler = new();
    private readonly MemoryTokenStore _tokens = new();
    private readonly List<Uri> _servers = [];
    private readonly ScrobbleQueue _queue;
    private readonly ListenBrainzScrobbler _scrobbler;

    public ListenBrainzScrobblerTests()
    {
        _queue = new ScrobbleQueue(_dir.Path, "listenbrainz-queue.json", "listenbrainz");
        _scrobbler = new ListenBrainzScrobbler(
            server =>
            {
                _servers.Add(server);
                return new ListenBrainzClient(server, "1.0.0", new HttpClient(_handler));
            },
            _queue, _tokens, _clock);
    }

    public void Dispose()
    {
        _scrobbler.Dispose();
        _dir.Dispose();
    }

    private async Task StartConnectedAsync()
    {
        _tokens.Saved = Saved;
        _scrobbler.Start();
        await _scrobbler.WhenIdleAsync();
    }

    private void Play(TrackInfo track, int seconds)
    {
        _scrobbler.OnStateChanged(PlaybackState.Playing);
        _scrobbler.OnNowPlayingChanged(track);
        _scrobbler.OnProgress(TimeSpan.Zero, track.Duration);
        for (var i = 1; i <= seconds; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            _scrobbler.OnProgress(TimeSpan.FromSeconds(i), track.Duration);
        }
    }

    private void SeedQueue(int count, bool sessionEnded = false)
    {
        var seeded = new ScrobbleQueue(_dir.Path, "listenbrainz-queue.json", "listenbrainz");
        for (var i = 0; i < count; i++)
            seeded.Enqueue(Tracks.Entry($"T{i}", 1_700_000_000 + i));
        if (sessionEnded)
            seeded.SessionEnded = true;
    }

    [Fact]
    public async Task EarnedListen_IsQueuedThenSentSingly_WithThePlayStartTime()
    {
        await StartConnectedAsync();

        Play(Tracks.Make(duration: TimeSpan.FromMinutes(3)), 90);
        await _scrobbler.WhenIdleAsync();

        var request = Assert.Single(_handler.Submits);
        Assert.Equal("Token TOKEN", request.Authorization);
        Assert.Equal("single", request.ListenType);
        var listen = request.Payload.Single();
        Assert.Equal(T0.ToUnixTimeSeconds(), listen.GetProperty("listened_at").GetInt64());
        var metadata = listen.GetProperty("track_metadata");
        Assert.Equal("Daft Punk", metadata.GetProperty("artist_name").GetString());   // first credit only
        Assert.Equal("Instant Crush", metadata.GetProperty("track_name").GetString());
        Assert.Equal(0, _scrobbler.PendingCount);
    }

    [Fact]
    public async Task NowPlaying_IsSentAsPlayingNow_AndNeverQueued()
    {
        await StartConnectedAsync();
        _handler.EnqueueNetworkFailure("submit-listens");

        Play(Tracks.Make(), 5);
        await _scrobbler.WhenIdleAsync();

        var request = Assert.Single(_handler.Requests, r => r.Endpoint == "submit-listens");
        Assert.Equal("playing_now", request.ListenType);
        Assert.Equal(0, _scrobbler.PendingCount);
        Assert.Equal(ListenBrainzConnectionState.Connected, _scrobbler.Connection);
    }

    [Fact]
    public async Task ShortTrack_IsNeitherAnnouncedNorSent()
    {
        await StartConnectedAsync();

        Play(Tracks.Make(duration: TimeSpan.FromSeconds(30)), 30);
        await _scrobbler.WhenIdleAsync();

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task NotConnected_SendsAndRecordsNothing()
    {
        _scrobbler.Start();
        await _scrobbler.WhenIdleAsync();

        Play(Tracks.Make(duration: TimeSpan.FromMinutes(2)), 120);
        await _scrobbler.WhenIdleAsync();

        Assert.Empty(_handler.Requests);
        Assert.Equal(0, _scrobbler.PendingCount);
        Assert.Equal(ListenBrainzConnectionState.Disconnected, _scrobbler.Connection);
    }

    [Fact]
    public async Task ScrobblingOff_SendsAndRecordsNothing_WhileStayingConnected()
    {
        await StartConnectedAsync();
        _scrobbler.ScrobblingEnabled = false;

        Play(Tracks.Make(duration: TimeSpan.FromMinutes(2)), 120);
        await _scrobbler.WhenIdleAsync();

        Assert.Empty(_handler.Requests);
        Assert.Equal("demo-listener", _scrobbler.Username);
        Assert.Equal(Home, _scrobbler.Server);
    }

    [Fact]
    public async Task Startup_DrainsTheSavedQueue_OldestFirst_AsImportBatchesOfAHundred()
    {
        SeedQueue(250);

        await StartConnectedAsync();

        var calls = _handler.Submits;
        Assert.Equal([100, 100, 50], calls.Select(c => c.Payload.Length));
        Assert.All(calls, c => Assert.Equal("import", c.ListenType));
        Assert.Equal("T0", calls[0].Tracks[0]);
        Assert.Equal("T99", calls[0].Tracks[99]);
        Assert.Equal("T249", calls[2].Tracks[49]);
        Assert.Equal(0, _scrobbler.PendingCount);
    }

    [Fact]
    public async Task Startup_UsesTheClientForTheSavedServer()
    {
        var selfHosted = new Uri("http://192.168.1.5:42010/apis/listenbrainz/");
        _tokens.Saved = Saved with { Server = selfHosted };

        _scrobbler.Start();
        await _scrobbler.WhenIdleAsync();

        Assert.Equal([selfHosted], _servers);
        Assert.Equal(selfHosted, _scrobbler.Server);
    }

    [Fact]
    public async Task Connect_ValidToken_SavesTheConnection_AndDrains()
    {
        SeedQueue(1);
        _scrobbler.Start();
        await _scrobbler.WhenIdleAsync();
        var server = new Uri("https://other.example/lb/");

        await _scrobbler.ConnectAsync(server, "  NEWTOKEN \n");

        Assert.Equal(new ListenBrainzConnection(server, "demo-listener", "NEWTOKEN"), _tokens.Saved);
        Assert.Equal([server], _servers);
        Assert.Equal(ListenBrainzConnectionState.Connected, _scrobbler.Connection);
        Assert.Equal("demo-listener", _scrobbler.Username);
        Assert.Equal(server, _scrobbler.Server);
        Assert.Null(_scrobbler.Notice);
        var send = Assert.Single(_handler.Submits);
        Assert.Equal("Token NEWTOKEN", send.Authorization);
        Assert.Equal("https://other.example/lb/1/submit-listens", send.Uri.AbsoluteUri);
        Assert.Equal(0, _scrobbler.PendingCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"code":200,"valid":false,"message":"no"}""", ListenBrainzNotice.ConnectTokenInvalid)]
    [InlineData(HttpStatusCode.Unauthorized, """{"code":401,"error":"no"}""", ListenBrainzNotice.ConnectTokenInvalid)]
    [InlineData(HttpStatusCode.OK, "<html></html>", ListenBrainzNotice.ConnectNotAServer)]
    [InlineData(HttpStatusCode.BadGateway, "oops", ListenBrainzNotice.ConnectUnreachable)]
    [InlineData(HttpStatusCode.TooManyRequests, "slow", ListenBrainzNotice.ConnectRateLimited)]
    [InlineData(HttpStatusCode.NotFound, "none", ListenBrainzNotice.ConnectRefused)]
    public async Task Connect_Failure_SavesNothing_AndSaysWhy(HttpStatusCode status, string body, ListenBrainzNotice expected)
    {
        _handler.Enqueue("validate-token", status, body);
        _scrobbler.Start();
        await _scrobbler.WhenIdleAsync();

        await _scrobbler.ConnectAsync(Home, "TOKEN");

        Assert.Equal(expected, _scrobbler.Notice);
        Assert.Equal(ListenBrainzConnectionState.Disconnected, _scrobbler.Connection);
        Assert.Null(_tokens.Saved);
        Assert.Empty(_handler.Submits);
    }

    [Fact]
    public async Task Connect_Unreachable_SavesNothing()
    {
        _handler.EnqueueNetworkFailure("validate-token");

        await _scrobbler.ConnectAsync(Home, "TOKEN");

        Assert.Equal(ListenBrainzNotice.ConnectUnreachable, _scrobbler.Notice);
        Assert.Null(_tokens.Saved);
    }

    [Fact]
    public async Task Connect_EmptyToken_IsInvalid_WithoutAskingTheServer()
    {
        await _scrobbler.ConnectAsync(Home, "   ");

        Assert.Equal(ListenBrainzNotice.ConnectTokenInvalid, _scrobbler.Notice);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Connect_FailureWhileConnected_KeepsTheWorkingConnection()
    {
        await StartConnectedAsync();
        _handler.Enqueue("validate-token", HttpStatusCode.OK, """{"valid":false}""");

        await _scrobbler.ConnectAsync(new Uri("https://other.example/"), "BAD");

        Assert.Equal(ListenBrainzConnectionState.Connected, _scrobbler.Connection);
        Assert.Equal(Saved, _tokens.Saved);
        Assert.Equal(ListenBrainzNotice.ConnectTokenInvalid, _scrobbler.Notice);
    }

    [Fact]
    public async Task ARefusedToken_DuringDrain_KeepsTheQueue_AndTheNextConnectSendsIt()
    {
        SeedQueue(2);
        _handler.EnqueueError("submit-listens", HttpStatusCode.Unauthorized);

        await StartConnectedAsync();

        Assert.Equal(ListenBrainzConnectionState.Disconnected, _scrobbler.Connection);
        Assert.Equal(ListenBrainzNotice.TokenRefused, _scrobbler.Notice);
        Assert.Null(_tokens.Saved);
        Assert.True(_scrobbler.SessionEnded);
        Assert.Equal(2, _scrobbler.PendingCount);

        // Later plays are still recorded while the connection is down.
        Play(Tracks.Make("Next", TimeSpan.FromMinutes(1)), 30);
        await _scrobbler.WhenIdleAsync();
        Assert.Equal(3, _scrobbler.PendingCount);
        Assert.Single(_handler.Submits);

        // A failed reconnect keeps both the notice's cause and the queue.
        _handler.Enqueue("validate-token", HttpStatusCode.OK, """{"valid":false}""");
        await _scrobbler.ConnectAsync(Home, "BAD");
        Assert.True(_scrobbler.SessionEnded);
        Assert.Equal(ListenBrainzNotice.ConnectTokenInvalid, _scrobbler.Notice);

        await _scrobbler.ConnectAsync(Home, "GOOD");

        var resent = _handler.Submits.Last();
        Assert.Equal(["T0", "T1", "Next"], resent.Tracks);
        Assert.Equal("Token GOOD", resent.Authorization);
        Assert.Equal(0, _scrobbler.PendingCount);
        Assert.False(_scrobbler.SessionEnded);
        Assert.Null(_scrobbler.Notice);
    }

    [Fact]
    public async Task ARefusedToken_OnNowPlaying_LosesTheConnection()
    {
        await StartConnectedAsync();
        _handler.EnqueueError("submit-listens", HttpStatusCode.Unauthorized);

        Play(Tracks.Make(), 1);
        await _scrobbler.WhenIdleAsync();

        Assert.Equal(ListenBrainzConnectionState.Disconnected, _scrobbler.Connection);
        Assert.Equal(ListenBrainzNotice.TokenRefused, _scrobbler.Notice);
        Assert.True(_scrobbler.SessionEnded);
    }

    [Fact]
    public async Task EndedBeforeARestart_StillShowsTheNotice()
    {
        SeedQueue(1, sessionEnded: true);

        _scrobbler.Start();
        await _scrobbler.WhenIdleAsync();

        Assert.Equal(ListenBrainzNotice.TokenRefused, _scrobbler.Notice);
        Assert.Empty(_handler.Requests);
        Assert.Equal(1, _scrobbler.PendingCount);
    }

    [Fact]
    public async Task ARefusedBatch_IsResentOneByOne_SoOnlyTheBadListenIsDropped()
    {
        SeedQueue(3);
        _handler.EnqueueError("submit-listens", HttpStatusCode.BadRequest);                   // the batch
        _handler.Enqueue("submit-listens", HttpStatusCode.OK, """{"status":"ok"}""");        // T0 alone
        _handler.EnqueueError("submit-listens", HttpStatusCode.BadRequest);                   // T1 alone

        await StartConnectedAsync();

        var sent = _handler.Submits;
        Assert.Equal(["import", "single", "single", "single"], sent.Select(s => s.ListenType));
        Assert.Equal(["T0", "T0", "T1", "T2"], sent.Select(s => s.Tracks[0]));
        Assert.Equal(0, _scrobbler.PendingCount);
        Assert.Equal(ListenBrainzConnectionState.Connected, _scrobbler.Connection);
    }

    [Fact]
    public async Task ServiceOffline_KeepsListens_AndRetriesAfterTheBackoff()
    {
        SeedQueue(1);
        _handler.EnqueueError("submit-listens", HttpStatusCode.ServiceUnavailable);
        await StartConnectedAsync();
        Assert.Equal(1, _scrobbler.PendingCount);
        Assert.Equal(ListenBrainzNotice.Unreachable, _scrobbler.Notice);

        Play(Tracks.Make("Next", TimeSpan.FromMinutes(1)), 30);
        await _scrobbler.WhenIdleAsync();
        Assert.Single(_handler.Submits);
        Assert.Equal(2, _scrobbler.PendingCount);

        _clock.Advance(ListenBrainzScrobbler.FirstRetryDelay);
        Play(Tracks.Make("Third", TimeSpan.FromMinutes(1)), 30);
        await _scrobbler.WhenIdleAsync();

        Assert.Equal(["T0", "Next", "Third"], _handler.Submits.Last().Tracks);
        Assert.Equal(0, _scrobbler.PendingCount);
        Assert.Null(_scrobbler.Notice);
    }

    [Fact]
    public async Task RateLimit_SlowsDown_ForLongerThanAnOutage_ThenSends()
    {
        SeedQueue(1);
        _handler.EnqueueError("submit-listens", HttpStatusCode.TooManyRequests);
        await StartConnectedAsync();

        _clock.Advance(ListenBrainzScrobbler.FirstRetryDelay);
        Play(Tracks.Make("Next", TimeSpan.FromMinutes(1)), 30);
        await _scrobbler.WhenIdleAsync();
        Assert.Single(_handler.Submits);
        Assert.Equal(ListenBrainzNotice.SlowedDown, _scrobbler.Notice);

        _clock.Advance(ListenBrainzScrobbler.FirstRateLimitDelay);
        Play(Tracks.Make("Third", TimeSpan.FromMinutes(1)), 30);
        await _scrobbler.WhenIdleAsync();

        Assert.Equal(2, _handler.Submits.Length);
        Assert.Equal(0, _scrobbler.PendingCount);
    }

    [Fact]
    public async Task Disconnect_ClearsTheStore_AndDiscardsTheQueue()
    {
        SeedQueue(2);
        _handler.EnqueueError("submit-listens", HttpStatusCode.ServiceUnavailable);
        await StartConnectedAsync();
        Assert.Equal(2, _scrobbler.PendingCount);
        _handler.Requests.Clear();

        await _scrobbler.DisconnectAsync();

        Assert.Null(_tokens.Saved);
        Assert.Equal(0, _scrobbler.PendingCount);
        Assert.False(_scrobbler.SessionEnded);
        Assert.Equal(ListenBrainzConnectionState.Disconnected, _scrobbler.Connection);
        Assert.Null(_scrobbler.Notice);
        Assert.Null(_scrobbler.Username);

        Play(Tracks.Make(duration: TimeSpan.FromMinutes(2)), 120);
        await _scrobbler.WhenIdleAsync();
        Assert.Equal(0, _scrobbler.PendingCount);
        Assert.Empty(_handler.Requests);
    }
}
