using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Integrations.LastFm;

namespace Rambutan.Integrations.Tests.LastFm;

public class ScrobbleGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 20, 0, 0, TimeSpan.Zero);

    private readonly ManualClock _clock = new(T0);
    private readonly ScrobbleGate _gate;
    private TimeSpan _position;
    private readonly List<ScrobbleCandidate> _earned = [];

    public ScrobbleGateTests() => _gate = new ScrobbleGate(_clock);

    private void Start(TrackInfo track)
    {
        _gate.OnStateChanged(PlaybackState.Playing);
        _gate.OnNowPlaying(track);
        _position = TimeSpan.Zero;
        Tick(TimeSpan.Zero, track.Duration);
    }

    /// <summary>Plays for <paramref name="seconds"/>, one progress event a second (the hub's rate).</summary>
    private void Play(int seconds, TimeSpan? duration = null, bool advancePosition = true)
    {
        for (var i = 0; i < seconds; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            if (advancePosition)
                _position += TimeSpan.FromSeconds(1);
            Tick(_position, duration ?? _gate.Track?.Duration);
        }
    }

    private void Tick(TimeSpan position, TimeSpan? duration)
    {
        if (_gate.OnProgress(position, duration) is { } candidate)
            _earned.Add(candidate);
    }

    private void Seek(TimeSpan to)
    {
        _position = to;
        Tick(_position, _gate.Track?.Duration);
    }

    [Fact]
    public void ScrobblesAtHalf_ForTracksUnderEightMinutes()
    {
        Start(Tracks.Make(duration: TimeSpan.FromMinutes(3)));

        Play(89);
        Assert.Empty(_earned);
        Play(1);

        var earned = Assert.Single(_earned);
        Assert.Equal(T0, earned.StartedAt);
        Assert.Equal(TimeSpan.FromMinutes(3), earned.Duration);
    }

    [Fact]
    public void ScrobblesAtFourMinutes_ForLongTracks()
    {
        Start(Tracks.Make(duration: TimeSpan.FromMinutes(20)));

        Play(239);
        Assert.Empty(_earned);
        Play(1);

        Assert.Single(_earned);
    }

    [Fact]
    public void ScrobblesOncePerPlay()
    {
        Start(Tracks.Make(duration: TimeSpan.FromMinutes(2)));

        Play(120);

        Assert.Single(_earned);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(12)]
    public void NeverScrobblesTracksOfThirtySecondsOrLess(int seconds)
    {
        Start(Tracks.Make(duration: TimeSpan.FromSeconds(seconds)));

        Play(seconds);

        Assert.Empty(_earned);
    }

    [Fact]
    public void ThirtyOneSecondTrack_ScrobblesAtHalf()
    {
        Start(Tracks.Make(duration: TimeSpan.FromSeconds(31)));

        Play(16);

        Assert.Single(_earned);
    }

    [Fact]
    public void UnknownDuration_UsesOnlyTheFourMinuteRule()
    {
        var track = Tracks.Make() with { Duration = null };
        Start(track);

        Play(239);
        Assert.Empty(_earned);
        Play(1);

        var earned = Assert.Single(_earned);
        Assert.Null(earned.Duration);
    }

    [Fact]
    public void DurationFromProgress_OverridesTheListedOne()
    {
        // Listed as 10 min, but the engine knows it's 2 min: the half rule uses 2.
        Start(Tracks.Make(duration: TimeSpan.FromMinutes(10)));

        Play(60, duration: TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(2), Assert.Single(_earned).Duration);
    }

    [Fact]
    public void SeekingForward_DoesNotCountAsListening()
    {
        Start(Tracks.Make(duration: TimeSpan.FromMinutes(4)));

        Play(10);
        Seek(TimeSpan.FromMinutes(3));  // straight past the halfway mark
        Play(10);

        Assert.Empty(_earned);
        Assert.Equal(TimeSpan.FromSeconds(20), _gate.Listened);
    }

    [Fact]
    public void SeekingBack_KeepsWhatWasHeard_AndCountsTheReplayedPart()
    {
        Start(Tracks.Make(duration: TimeSpan.FromMinutes(4)));

        Play(100);
        Seek(TimeSpan.FromSeconds(10));
        Play(20);

        // 100 s + 20 s heard; peak position never passed 100 s.
        Assert.Single(_earned);
    }

    [Fact]
    public void PausedTime_DoesNotCount()
    {
        Start(Tracks.Make(duration: TimeSpan.FromMinutes(4)));

        Play(60);
        _gate.OnStateChanged(PlaybackState.Paused);
        Play(600, advancePosition: false);
        _gate.OnStateChanged(PlaybackState.Playing);
        Play(59);

        Assert.Empty(_earned);
        Play(2);  // the first step after resuming only re-establishes the baseline
        Assert.Single(_earned);
    }

    [Fact]
    public void AJumpWhilePaused_IsNotBridgedOnResume()
    {
        Start(Tracks.Make(duration: TimeSpan.FromMinutes(4)));

        Play(30);
        _gate.OnStateChanged(PlaybackState.Paused);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Seek(TimeSpan.FromMinutes(3));
        _gate.OnStateChanged(PlaybackState.Playing);
        Play(5);

        // 30 s before the pause; after it, the first step only sets the baseline.
        Assert.Equal(TimeSpan.FromSeconds(34), _gate.Listened);
    }

    [Fact]
    public void Buffering_DoesNotCount()
    {
        Start(Tracks.Make(duration: TimeSpan.FromMinutes(4)));

        Play(10);
        _gate.OnStateChanged(PlaybackState.Buffering);
        Play(5);

        Assert.Equal(TimeSpan.FromSeconds(10), _gate.Listened);
    }

    [Fact]
    public void RepeatOneReplay_IsANewPlay_WithItsOwnTimestamp()
    {
        var track = Tracks.Make(duration: TimeSpan.FromMinutes(2));
        Start(track);
        Play(120);

        // Repeat-one: the coordinator announces the same track again, still playing.
        _gate.OnNowPlaying(track);
        var replayStart = _clock.GetUtcNow();
        _position = TimeSpan.Zero;
        Tick(_position, track.Duration);
        Play(60);

        Assert.Equal(2, _earned.Count);
        Assert.Equal(T0, _earned[0].StartedAt);
        Assert.Equal(replayStart, _earned[1].StartedAt);
    }

    [Fact]
    public void ANewTrack_ResetsListening()
    {
        Start(Tracks.Make("A", TimeSpan.FromMinutes(4)));
        Play(100);
        Start(Tracks.Make("B", TimeSpan.FromMinutes(4)));
        Play(100);

        Assert.Empty(_earned);
    }

    [Fact]
    public void PlaybackEnded_EarnsNothing()
    {
        Start(Tracks.Make(duration: TimeSpan.FromMinutes(4)));
        _gate.OnNowPlaying(null);

        Play(300);

        Assert.Empty(_earned);
    }

    [Theory]
    [InlineData(20, null)]
    [InlineData(31, 15.5)]
    [InlineData(300, 150.0)]
    [InlineData(480, 240.0)]
    [InlineData(3600, 240.0)]
    public void Threshold_IsHalfCappedAtFourMinutes(int durationSeconds, double? expectedSeconds)
    {
        Assert.Equal(
            expectedSeconds is { } e ? TimeSpan.FromSeconds(e) : null,
            ScrobbleGate.Threshold(TimeSpan.FromSeconds(durationSeconds)));
    }
}
