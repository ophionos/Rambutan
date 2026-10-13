using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Integrations.LastFm;

namespace Rambutan.Integrations.Tests.LastFm;

public sealed class LastFmScrobblerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 20, 0, 0, TimeSpan.Zero);
    private static readonly LastFmSession Session = new("DemoListener", "SK");

    private readonly TempDir _dir = new();
    private readonly ManualClock _clock = new(T0);
    private readonly FakeLastFmHandler _handler = new();
    private readonly MemorySessionStore _sessions = new();
    private readonly ScrobbleQueue _queue;
    private readonly LastFmScrobbler _scrobbler;

    public LastFmScrobblerTests()
    {
        _queue = new ScrobbleQueue(_dir.Path);
        _scrobbler = new LastFmScrobbler(
            new LastFmClient("k", "s", new HttpClient(_handler)), _queue, _sessions, _clock);
    }

    public void Dispose()
    {
        _scrobbler.Dispose();
        _dir.Dispose();
    }

    private async Task StartConnectedAsync()
    {
        _sessions.Saved = Session;
        _scrobbler.Start();
        await _scrobbler.WhenIdleAsync();
    }

    /// <summary>Plays <paramref name="track"/> from the top for <paramref name="seconds"/>, at the hub's 1 Hz.</summary>
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

    private void SeedQueue(int count)
    {
        var seeded = new ScrobbleQueue(_dir.Path);
        for (var i = 0; i < count; i++)
            seeded.Enqueue(Tracks.Entry($"T{i}", 1_700_000_000 + i));
    }

    [Fact]
    public async Task EarnedScrobble_IsQueuedThenSent_WithThePlayStartTimestamp()
    {
        await StartConnectedAsync();

        Play(Tracks.Make(duration: TimeSpan.FromMinutes(3)), 90);
        await _scrobbler.WhenIdleAsync();

        var form = _handler.Calls("track.scrobble").Single().Form;
        Assert.Equal("SK", form["sk"]);
        Assert.Equal("Daft Punk", form["artist[0]"]);  // first credit only
        Assert.Equal("Instant Crush", form["track[0]"]);
        Assert.Equal("Random Access Memories", form["album[0]"]);
        Assert.Equal("180", form["duration[0]"]);
        Assert.Equal(T0.ToUnixTimeSeconds().ToString(), form["timestamp[0]"]);
        Assert.Equal(0, _scrobbler.PendingCount);
    }

    [Fact]
    public async Task NowPlaying_IsSentOnTrackStart_AndNeverQueued()
    {
        await StartConnectedAsync();
        _handler.EnqueueNetworkFailure("track.updateNowPlaying");

        Play(Tracks.Make(), 5);
        await _scrobbler.WhenIdleAsync();

        Assert.Single(_handler.Calls("track.updateNowPlaying"));
        Assert.Equal(0, _scrobbler.PendingCount);
        Assert.Equal(LastFmConnection.Connected, _scrobbler.Connection);
    }

    [Fact]
    public async Task ShortTrack_IsNeitherAnnouncedNorScrobbled()
    {
        await StartConnectedAsync();

        Play(Tracks.Make(duration: TimeSpan.FromSeconds(30)), 30);
        await _scrobbler.WhenIdleAsync();

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task UnknownLength_IsStillAnnounced()
    {
        await StartConnectedAsync();

        Play(Tracks.Make() with { Duration = null }, 5);
        await _scrobbler.WhenIdleAsync();

        Assert.Single(_handler.Calls("track.updateNowPlaying"));
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
        Assert.Equal(LastFmConnection.Disconnected, _scrobbler.Connection);
    }

    [Fact]
    public async Task ScrobblingOff_SendsAndRecordsNothing_WhileStayingConnected()
    {
        await StartConnectedAsync();
        _scrobbler.ScrobblingEnabled = false;

        Play(Tracks.Make(duration: TimeSpan.FromMinutes(2)), 120);
        await _scrobbler.WhenIdleAsync();

        Assert.Empty(_handler.Requests);
        Assert.Equal("DemoListener", _scrobbler.Username);
    }

    [Fact]
    public async Task Startup_DrainsTheSavedQueue_OldestFirst_InBatchesOfFifty()
    {
        SeedQueue(120);

        await StartConnectedAsync();

        var calls = _handler.Calls("track.scrobble").ToArray();
        Assert.Equal(3, calls.Length);
        Assert.Equal("T0", calls[0].Form["track[0]"]);
        Assert.Equal("T49", calls[0].Form["track[49]"]);
        Assert.Equal("T50", calls[1].Form["track[0]"]);
        Assert.Equal("T100", calls[2].Form["track[0]"]);
        Assert.False(calls[2].Form.ContainsKey("track[20]"));
        Assert.Equal(0, _scrobbler.PendingCount);
        Assert.Null(_scrobbler.Notice);
    }

    [Fact]
    public async Task InvalidSession_Disconnects_KeepsTheQueue_AndReconnectDrainsIt()
    {
        SeedQueue(2);
        _handler.EnqueueError("track.scrobble", 9, System.Net.HttpStatusCode.Forbidden);

        await StartConnectedAsync();

        Assert.Equal(LastFmConnection.Disconnected, _scrobbler.Connection);
        Assert.Null(_sessions.Saved);
        Assert.Equal(2, _scrobbler.PendingCount);
        Assert.Equal(LastFmNotice.SessionEnded, _scrobbler.Notice);

        var url = await _scrobbler.BeginConnectAsync();
        Assert.Equal(LastFmConnection.AwaitingApproval, _scrobbler.Connection);
        Assert.StartsWith("https://www.last.fm/api/auth/?api_key=k&token=", url);
        await _scrobbler.FinishConnectAsync();

        Assert.Equal(LastFmConnection.Connected, _scrobbler.Connection);
        Assert.Equal("DemoListener", _scrobbler.Username);
        Assert.Equal("DemoListener", _sessions.Saved?.Username);
        Assert.Equal(0, _scrobbler.PendingCount);
        Assert.Equal(2, _handler.Calls("track.scrobble").Count());
    }

    [Theory]
    [InlineData(11)]
    [InlineData(16)]
    public async Task ServiceOffline_KeepsScrobbles_AndRetriesAfterTheBackoff(int code)
    {
        SeedQueue(1);
        _handler.EnqueueError("track.scrobble", code, System.Net.HttpStatusCode.ServiceUnavailable);
        await StartConnectedAsync();
        Assert.Equal(1, _scrobbler.PendingCount);
        Assert.Equal(LastFmNotice.Unreachable, _scrobbler.Notice);

        // Another play earned during the backoff queues without calling out.
        Play(Tracks.Make("Next", TimeSpan.FromMinutes(1)), 30);
        await _scrobbler.WhenIdleAsync();
        Assert.Single(_handler.Calls("track.scrobble"));
        Assert.Equal(2, _scrobbler.PendingCount);

        _clock.Advance(LastFmScrobbler.FirstRetryDelay);
        Play(Tracks.Make("Third", TimeSpan.FromMinutes(1)), 30);
        await _scrobbler.WhenIdleAsync();

        var retry = _handler.Calls("track.scrobble").Last();
        Assert.Equal(["T0", "Next", "Third"], [retry.Form["track[0]"], retry.Form["track[1]"], retry.Form["track[2]"]]);
        Assert.Equal(0, _scrobbler.PendingCount);
        Assert.Null(_scrobbler.Notice);
    }

    [Fact]
    public async Task ARetryTimerThatFiresEarly_WaitsOutTheRest_InsteadOfStalling()
    {
        // A reconnect during an outage restarts the backoff while the first retry
        // timer is still set, so that timer fires before the new target. It used
        // to drain nothing then and leave no timer, stalling the queue.
        var clock = new ManualClock(T0, runsTimers: true);
        using var dir = new TempDir();
        var queue = new ScrobbleQueue(dir.Path);
        using var scrobbler = new LastFmScrobbler(
            new LastFmClient("k", "s", new HttpClient(_handler)), queue,
            new MemorySessionStore { Saved = Session }, clock);
        new ScrobbleQueue(dir.Path).Enqueue(Tracks.Entry("T0", 1_700_000_000));

        _handler.EnqueueError("track.scrobble", 11, System.Net.HttpStatusCode.ServiceUnavailable);
        scrobbler.Start();
        await scrobbler.WhenIdleAsync();
        Assert.Equal(1, clock.PendingTimers);                        // due at T0 + 1 min

        clock.Advance(TimeSpan.FromSeconds(30));
        await scrobbler.DisconnectAsync();
        await scrobbler.BeginConnectAsync();
        await scrobbler.FinishConnectAsync();
        _handler.EnqueueError("track.scrobble", 11, System.Net.HttpStatusCode.ServiceUnavailable);
        var track = Tracks.Make("Earned", TimeSpan.FromSeconds(40));
        scrobbler.OnStateChanged(PlaybackState.Playing);
        scrobbler.OnNowPlayingChanged(track);
        for (var i = 0; i <= 20; i++)
        {
            scrobbler.OnProgress(TimeSpan.FromSeconds(i), track.Duration);
            if (i < 20)
                clock.Advance(TimeSpan.FromSeconds(1));
        }
        await scrobbler.WhenIdleAsync();
        Assert.Equal(1, scrobbler.PendingCount);                     // retry target now T0 + 1:50
        Assert.Equal(2, _handler.Calls("track.scrobble").Count());

        clock.Advance(TimeSpan.FromSeconds(10));                     // the old timer: early
        await Until(() => clock.PendingTimers == 1);
        Assert.Equal(2, _handler.Calls("track.scrobble").Count());

        clock.Advance(TimeSpan.FromSeconds(50));
        await Until(() => scrobbler.PendingCount == 0);
        Assert.Equal(3, _handler.Calls("track.scrobble").Count());
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task NetworkFailure_KeepsScrobbles()
    {
        SeedQueue(3);
        _handler.EnqueueNetworkFailure("track.scrobble");

        await StartConnectedAsync();

        Assert.Equal(3, _scrobbler.PendingCount);
        Assert.Equal(LastFmConnection.Connected, _scrobbler.Connection);
    }

    [Fact]
    public async Task RateLimit_BacksOffLongerThanAnOutage()
    {
        SeedQueue(1);
        _handler.EnqueueError("track.scrobble", 29);
        await StartConnectedAsync();

        _clock.Advance(LastFmScrobbler.FirstRetryDelay);
        Play(Tracks.Make("Next", TimeSpan.FromMinutes(1)), 30);
        await _scrobbler.WhenIdleAsync();
        Assert.Single(_handler.Calls("track.scrobble"));
        Assert.Equal(LastFmNotice.SlowedDown, _scrobbler.Notice);

        _clock.Advance(LastFmScrobbler.FirstRateLimitDelay);
        Play(Tracks.Make("Third", TimeSpan.FromMinutes(1)), 30);
        await _scrobbler.WhenIdleAsync();
        Assert.Equal(2, _handler.Calls("track.scrobble").Count());
        Assert.Equal(0, _scrobbler.PendingCount);
    }

    [Fact]
    public async Task ARefusedBatch_IsResentOneByOne_SoOnlyTheBadPlayIsDropped()
    {
        SeedQueue(3);
        _handler.EnqueueError("track.scrobble", 6);   // the batch
        _handler.Enqueue("track.scrobble", System.Net.HttpStatusCode.OK, FakeLastFmHandler.Fixture("track.scrobble.single"));
        _handler.EnqueueError("track.scrobble", 6);   // T1 alone
        _handler.Enqueue("track.scrobble", System.Net.HttpStatusCode.OK, FakeLastFmHandler.Fixture("track.scrobble.single"));

        await StartConnectedAsync();

        var sent = _handler.Calls("track.scrobble").Select(c => c.Form["track[0]"]).ToArray();
        Assert.Equal(["T0", "T0", "T1", "T2"], sent);
        Assert.Equal(0, _scrobbler.PendingCount);
        Assert.Equal(LastFmConnection.Connected, _scrobbler.Connection);
    }

    [Fact]
    public async Task ARefusedSingleScrobble_IsDropped_AndTheNextOneStillGoes()
    {
        SeedQueue(1);
        _handler.EnqueueError("track.scrobble", 6);
        await StartConnectedAsync();
        Assert.Equal(0, _scrobbler.PendingCount);

        Play(Tracks.Make(duration: TimeSpan.FromMinutes(1)), 30);
        await _scrobbler.WhenIdleAsync();

        Assert.Equal(2, _handler.Calls("track.scrobble").Count());
    }

    [Fact]
    public async Task BadApiKey_KeepsEverything_AndStopsCalling()
    {
        SeedQueue(2);
        _handler.EnqueueError("track.scrobble", 10, System.Net.HttpStatusCode.Forbidden);
        await StartConnectedAsync();

        Play(Tracks.Make(duration: TimeSpan.FromMinutes(1)), 30);
        await _scrobbler.WhenIdleAsync();

        Assert.Single(_handler.Calls("track.scrobble"));
        Assert.Empty(_handler.Calls("track.updateNowPlaying"));
        Assert.Equal(3, _scrobbler.PendingCount);
        Assert.Equal(LastFmNotice.KeyRefused, _scrobbler.Notice);
    }

    [Fact]
    public async Task Finishing_BeforeApproval_KeepsWaiting()
    {
        _scrobbler.Start();
        await _scrobbler.BeginConnectAsync();
        _handler.EnqueueError("auth.getSession", 14);

        await _scrobbler.FinishConnectAsync();

        Assert.Equal(LastFmConnection.AwaitingApproval, _scrobbler.Connection);
        Assert.Equal(LastFmNotice.NotApprovedYet, _scrobbler.Notice);

        await _scrobbler.FinishConnectAsync();
        Assert.Equal(LastFmConnection.Connected, _scrobbler.Connection);
        Assert.Null(_scrobbler.Notice);
    }

    [Fact]
    public async Task AnExpiredToken_StartsOver()
    {
        _scrobbler.Start();
        await _scrobbler.BeginConnectAsync();
        _handler.EnqueueError("auth.getSession", 15);

        await _scrobbler.FinishConnectAsync();

        Assert.Equal(LastFmConnection.Disconnected, _scrobbler.Connection);
        Assert.Equal(LastFmNotice.ApprovalExpired, _scrobbler.Notice);
    }

    [Fact]
    public async Task ConnectWhileOffline_SaysSo_AndStaysDisconnected()
    {
        _scrobbler.Start();
        _handler.EnqueueNetworkFailure("auth.getToken");

        var url = await _scrobbler.BeginConnectAsync();

        Assert.Null(url);
        Assert.Equal(LastFmConnection.Disconnected, _scrobbler.Connection);
        Assert.Equal(LastFmNotice.ConnectUnreachable, _scrobbler.Notice);
    }

    [Fact]
    public async Task Cancel_ReturnsToDisconnected()
    {
        _scrobbler.Start();
        await _scrobbler.BeginConnectAsync();

        await _scrobbler.CancelConnectAsync();
        await _scrobbler.FinishConnectAsync();

        Assert.Equal(LastFmConnection.Disconnected, _scrobbler.Connection);
        Assert.Empty(_handler.Calls("auth.getSession"));
    }

    /// <summary>Connected with an empty queue; Last.fm then ends the session on
    /// the next track's "now playing" (a case found in a live check).</summary>
    private async Task EndSessionOnNowPlayingAsync()
    {
        await StartConnectedAsync();
        _handler.EnqueueError("track.updateNowPlaying", 9, System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AfterLastFmEndsTheSession_PlaysAreStillRecorded_AndSentOnReconnect()
    {
        await EndSessionOnNowPlayingAsync();

        Play(Tracks.Make(duration: TimeSpan.FromMinutes(3)), 90);
        await _scrobbler.WhenIdleAsync();

        Assert.Equal(LastFmConnection.Disconnected, _scrobbler.Connection);
        Assert.True(_scrobbler.SessionEnded);
        Assert.Equal(LastFmNotice.SessionEnded, _scrobbler.Notice);
        Assert.Equal(1, _scrobbler.PendingCount);
        Assert.Empty(_handler.Calls("track.scrobble"));

        await _scrobbler.BeginConnectAsync();
        await _scrobbler.FinishConnectAsync();

        Assert.Equal(LastFmConnection.Connected, _scrobbler.Connection);
        Assert.False(_scrobbler.SessionEnded);
        Assert.Equal(0, _scrobbler.PendingCount);
        var form = _handler.Calls("track.scrobble").Single().Form;
        Assert.Equal(T0.ToUnixTimeSeconds().ToString(), form["timestamp[0]"]);
    }

    [Fact]
    public async Task TheEndedState_OutlastsARestart()
    {
        await EndSessionOnNowPlayingAsync();
        Play(Tracks.Make(duration: TimeSpan.FromMinutes(3)), 90);
        await _scrobbler.WhenIdleAsync();

        using var restarted = new LastFmScrobbler(
            new LastFmClient("k", "s", new HttpClient(_handler)), new ScrobbleQueue(_dir.Path), _sessions, _clock);
        restarted.Start();
        await restarted.WhenIdleAsync();

        Assert.Equal(LastFmConnection.Disconnected, restarted.Connection);
        Assert.True(restarted.SessionEnded);
        Assert.Equal(LastFmNotice.SessionEnded, restarted.Notice);
        Assert.Equal(1, restarted.PendingCount);

        restarted.OnStateChanged(PlaybackState.Playing);
        var second = Tracks.Make("Get Lucky", TimeSpan.FromMinutes(3));
        restarted.OnNowPlayingChanged(second);
        restarted.OnProgress(TimeSpan.Zero, second.Duration);
        for (var i = 1; i <= 90; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            restarted.OnProgress(TimeSpan.FromSeconds(i), second.Duration);
        }
        await restarted.WhenIdleAsync();
        Assert.Equal(2, restarted.PendingCount);
    }

    [Fact]
    public async Task AFailedReconnect_KeepsRecording()
    {
        await EndSessionOnNowPlayingAsync();
        Play(Tracks.Make(duration: TimeSpan.FromMinutes(3)), 90);
        await _scrobbler.WhenIdleAsync();

        await _scrobbler.BeginConnectAsync();
        await _scrobbler.CancelConnectAsync();

        Assert.True(_scrobbler.SessionEnded);
        Assert.Equal(LastFmNotice.SessionEnded, _scrobbler.Notice);

        _handler.EnqueueNetworkFailure("auth.getToken");
        await _scrobbler.BeginConnectAsync();
        Assert.Equal(LastFmNotice.ConnectUnreachable, _scrobbler.Notice);
        Assert.True(_scrobbler.SessionEnded);
        Assert.Equal(1, _scrobbler.PendingCount);
    }

    [Fact]
    public async Task DisconnectAfterLastFmEndedTheSession_DiscardsThePlays_AndStopsRecording()
    {
        await EndSessionOnNowPlayingAsync();
        Play(Tracks.Make(duration: TimeSpan.FromMinutes(3)), 90);
        await _scrobbler.WhenIdleAsync();

        await _scrobbler.DisconnectAsync();
        Assert.False(_scrobbler.SessionEnded);
        Assert.Null(_scrobbler.Notice);
        Assert.Equal(0, _scrobbler.PendingCount);

        Play(Tracks.Make("Get Lucky", TimeSpan.FromMinutes(3)), 90);
        await _scrobbler.WhenIdleAsync();
        Assert.Equal(0, _scrobbler.PendingCount);
        var reloaded = new ScrobbleQueue(_dir.Path);
        reloaded.Load();
        Assert.False(reloaded.SessionEnded);
    }

    [Fact]
    public async Task ScrobblingOff_AfterLastFmEndedTheSession_RecordsNothing()
    {
        await EndSessionOnNowPlayingAsync();
        _scrobbler.ScrobblingEnabled = false;

        Play(Tracks.Make(duration: TimeSpan.FromMinutes(3)), 90);
        await _scrobbler.WhenIdleAsync();

        Assert.Equal(0, _scrobbler.PendingCount);
    }

    [Fact]
    public async Task Disconnect_ForgetsTheSession_AndDiscardsUnsentScrobbles()
    {
        SeedQueue(2);
        _handler.EnqueueNetworkFailure("track.scrobble");
        await StartConnectedAsync();

        await _scrobbler.DisconnectAsync();

        Assert.Equal(LastFmConnection.Disconnected, _scrobbler.Connection);
        Assert.Null(_sessions.Saved);
        Assert.Equal(0, _scrobbler.PendingCount);
        var reloaded = new ScrobbleQueue(_dir.Path);
        reloaded.Load();
        Assert.Equal(0, reloaded.Count);
    }

    [Fact]
    public void ToEntry_UsesTheFirstNamedArtist_AndRoundsTheLength()
    {
        var track = Tracks.Make(duration: TimeSpan.FromSeconds(337.6), album: " ", artists: ["", "Daft Punk", "Pharrell"]);

        var entry = LastFmScrobbler.ToEntry(track, T0);

        Assert.NotNull(entry);
        Assert.Equal("Daft Punk", entry.Artist);
        Assert.Null(entry.Album);
        Assert.Equal(338, entry.DurationSeconds);
        Assert.Equal(T0.ToUnixTimeSeconds(), entry.Timestamp);
    }

    [Fact]
    public void ToEntry_SkipsTracksWithNoArtist()
    {
        var track = Tracks.Make() with { Artists = [] };

        Assert.Null(LastFmScrobbler.ToEntry(track, T0));
    }
}
