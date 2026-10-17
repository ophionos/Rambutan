using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;
using Rambutan.Core.Remote;
using Rambutan.Core.Settings;

namespace Rambutan.Core.Tests;

public class RemoteSessionTests
{
    private readonly FakeStreamSource _streams = new();
    private readonly FakeAudioPlayer _player = new();
    private readonly QueueModel _queue = new();
    private readonly PlaybackSettings _settings = new();
    private TimeSpan _now = TimeSpan.FromSeconds(100);
    private readonly List<Action> _dispatched = [];

    private PlaybackCoordinator CreateCoordinator() => new(_streams, _player, _queue, _settings);

    private RemoteSession CreateSession(PlaybackCoordinator coordinator, bool runCommands = true) =>
        new(coordinator, action =>
        {
            _dispatched.Add(action);
            if (runCommands)
                action();
        }, hasArtwork: t => t.VideoId != "noart", clock: () => _now);

    private static IReadOnlyList<TrackInfo> Tracks(params string[] ids) =>
        ids.Select(id => TestTracks.Track(id)).ToArray();

    [Fact]
    public void StartsFromWhatTheCoordinatorHas()
    {
        _settings.Volume = 0.4;
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);

        Assert.Equal(new RemoteState("stopped", null, 0, null, 0.4, false), session.Current);
    }

    [Fact]
    public async Task PlayingATrack_PublishesTheTrackAndState()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        var changes = 0;
        session.Changed += () => changes++;

        await coordinator.PlayQueueAsync([TestTracks.Track("t1") with { Album = "Album" }]);

        var state = session.Current;
        Assert.Equal("playing", state.Playback);
        Assert.Equal(new RemoteTrack("t1", "Title t1", "Artist", "Album", 100_000, true), state.Track);
        Assert.Equal(100_000, state.DurationMs);
        Assert.True(changes > 0);
    }

    [Fact]
    public void TheAccent_GoesOutWithTheState_AndOnlyAChangePublishes()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        var changes = 0;
        session.Changed += () => changes++;
        Assert.Equal(RemoteState.DefaultAccent, session.Current.Accent);

        session.Accent = "#5B8DEF";
        session.Accent = "#5B8DEF";

        Assert.Equal("#5B8DEF", session.Current.Accent);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Artwork_IsWhatTheHostSays()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);

        await coordinator.PlayQueueAsync(Tracks("noart"));

        Assert.False(session.Current.Track!.HasArtwork);
    }

    [Fact]
    public async Task Progress_InStepWithTheClientsEstimate_IsNotPublished()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        await coordinator.PlayQueueAsync(Tracks("t1"));
        var changes = 0;
        session.Changed += () => changes++;

        // Two seconds pass and the track moves two seconds: the phone's own
        // estimate already says so.
        _now += TimeSpan.FromSeconds(2);
        _player.RaiseProgress(TimeSpan.FromSeconds(2));

        Assert.Equal(0, changes);
        Assert.Equal(0, session.Current.PositionMs);
    }

    [Fact]
    public async Task Progress_ThatJumps_IsPublished()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        await coordinator.PlayQueueAsync(Tracks("t1"));
        var changes = 0;
        session.Changed += () => changes++;

        _now += TimeSpan.FromSeconds(1);
        _player.RaiseProgress(TimeSpan.FromSeconds(40)); // a seek from the desktop

        Assert.Equal(1, changes);
        Assert.Equal(40_000, session.Current.PositionMs);
    }

    [Fact]
    public async Task Progress_IsResentAfterTheResyncInterval()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        await coordinator.PlayQueueAsync(Tracks("t1"));
        var changes = 0;
        session.Changed += () => changes++;

        _now += RemoteSession.ResyncInterval;
        _player.RaiseProgress(RemoteSession.ResyncInterval);

        Assert.Equal(1, changes);
        Assert.Equal((long)RemoteSession.ResyncInterval.TotalMilliseconds, session.Current.PositionMs);
    }

    [Fact]
    public async Task Progress_WhilePaused_IsEstimatedAsStandingStill()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        await coordinator.PlayQueueAsync(Tracks("t1"));
        coordinator.Pause();
        var changes = 0;
        session.Changed += () => changes++;

        _now += TimeSpan.FromSeconds(3);
        _player.RaiseProgress(TimeSpan.Zero);

        Assert.Equal(0, changes);
        Assert.Equal("paused", session.Current.Playback);
    }

    [Fact]
    public void VolumeFromAnySurface_IsPublished()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        var changes = 0;
        session.Changed += () => changes++;

        coordinator.Volume = 0.25;

        Assert.Equal(1, changes);
        Assert.Equal(0.25, session.Current.Volume);
    }

    [Fact]
    public async Task Commands_GoThroughTheDispatcher()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator, runCommands: false);
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));

        session.Execute(new RemoteCommand.Pause());

        Assert.Single(_dispatched);
        Assert.Equal(PlaybackState.Playing, coordinator.State);
        _dispatched[0]();
        Assert.Equal(PlaybackState.Paused, coordinator.State);
    }

    [Fact]
    public async Task PlayAndPause_AreNotAToggle()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        await coordinator.PlayQueueAsync(Tracks("t1"));

        session.Execute(new RemoteCommand.Play());
        Assert.Equal(PlaybackState.Playing, coordinator.State);

        session.Execute(new RemoteCommand.Pause());
        session.Execute(new RemoteCommand.Pause());
        Assert.Equal(PlaybackState.Paused, coordinator.State);

        session.Execute(new RemoteCommand.Play());
        Assert.Equal(PlaybackState.Playing, coordinator.State);
        Assert.Equal("playing", session.Current.Playback);
    }

    [Fact]
    public async Task Play_WhenStoppedWithAQueue_StartsItsCurrentTrack()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        _queue.SetQueue(Tracks("t1", "t2"), 1);

        session.Execute(new RemoteCommand.Play());

        await TestTracks.WaitUntilAsync(() => coordinator.State == PlaybackState.Playing);
        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
    }

    [Fact]
    public async Task NextPreviousSeekAndVolume_ReachTheCoordinator()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));

        session.Execute(new RemoteCommand.Next());
        await TestTracks.WaitUntilAsync(() => coordinator.NowPlaying?.VideoId == "t2");

        session.Execute(new RemoteCommand.Seek(TimeSpan.FromSeconds(30)));
        await TestTracks.WaitUntilAsync(() => _player.Log.Contains("seek:30"));

        session.Execute(new RemoteCommand.Previous()); // past 3 s: restarts the track
        await TestTracks.WaitUntilAsync(() => _player.Log.Contains("seek:0"));

        session.Execute(new RemoteCommand.SetVolume(1.7));
        Assert.Equal(1.0, coordinator.Volume);
        session.Execute(new RemoteCommand.SetVolume(0.5));
        Assert.Equal(0.5, coordinator.Volume);
    }

    [Fact]
    public async Task Dispose_StopsFollowingTheCoordinator()
    {
        using var coordinator = CreateCoordinator();
        var session = CreateSession(coordinator);
        var changes = 0;
        session.Changed += () => changes++;
        session.Dispose();

        await coordinator.PlayQueueAsync(Tracks("t1"));
        coordinator.Volume = 0.1;

        Assert.Equal(0, changes);
    }
}

public class PairingTokenTests
{
    [Fact]
    public void Tokens_AreLongRandomAndUrlSafe()
    {
        var a = PairingToken.Create();
        var b = PairingToken.Create();

        Assert.NotEqual(a, b);
        Assert.True(PairingToken.IsWellFormed(a));
        Assert.Equal(43, a.Length);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("0123456789012345678901234567890123456789012")] // right length
    public void OnlyTheIssuedTokenMatches(string? presented)
    {
        var token = PairingToken.Create();

        Assert.True(PairingToken.Matches(token, token));
        Assert.False(PairingToken.Matches(token, presented));
    }

    [Fact]
    public void NoTokenIssued_MatchesNothing()
    {
        Assert.False(PairingToken.Matches(null, ""));
        Assert.False(PairingToken.Matches("", ""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has spaces in it and is the right length....")]
    [InlineData("abc")]
    public void DamagedStoredTokens_AreNotWellFormed(string? stored) =>
        Assert.False(PairingToken.IsWellFormed(stored));
}
