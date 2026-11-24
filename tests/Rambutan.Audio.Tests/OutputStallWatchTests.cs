namespace Rambutan.Audio.Tests;

/// <summary>When an output device that stopped asking for audio is reopened,
/// and when the engine gives up and calls the output lost.</summary>
public class OutputStallWatchTests
{
    private static readonly TimeSpan Check = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Pull = TimeSpan.FromMilliseconds(20);

    private readonly OutputStallWatch _watch = new();
    private TimeSpan _now = TimeSpan.FromSeconds(100);
    private TimeSpan? _lastPull;

    private OutputStallVerdict Tick(bool pulling)
    {
        _now += Check;
        if (pulling)
            _lastPull = _now - Pull;
        return _watch.Check(_now, _lastPull);
    }

    /// <summary>Checks until the watch acts, for at most <paramref name="limit"/>.</summary>
    private (OutputStallVerdict Verdict, TimeSpan After) UntilItActs(TimeSpan limit)
    {
        var from = _now;
        while (_now - from < limit)
        {
            if (Tick(pulling: false) is not OutputStallVerdict.Fine and var verdict)
                return (verdict, _now - from);
        }
        return (OutputStallVerdict.Fine, _now - from);
    }

    private void StartAndPlay()
    {
        _watch.Started(_now);
        for (var i = 0; i < 4; i++)
            Assert.Equal(OutputStallVerdict.Fine, Tick(pulling: true));
    }

    [Fact]
    public void ADeviceThatKeepsPulling_IsLeftAlone()
    {
        _watch.Started(_now);
        for (var i = 0; i < 1000; i++)
            Assert.Equal(OutputStallVerdict.Fine, Tick(pulling: true));
    }

    [Fact]
    public void ADeviceThatStopsPulling_IsReopened_OnceTwoChecksFindItSilentPastTheLimit()
    {
        StartAndPlay();
        var silentSince = _lastPull!.Value;

        var (verdict, _) = UntilItActs(TimeSpan.FromMinutes(1));

        Assert.Equal(OutputStallVerdict.Reopen, verdict);
        // Not before the limit, and no later than two checks past it.
        Assert.InRange(_now - silentSince, OutputStallWatch.StallAfter, OutputStallWatch.StallAfter + 2 * Check);
    }

    [Fact]
    public void AStartThatNeverPulls_GetsTheStartGrace_NotTheShorterLimit()
    {
        _watch.Started(_now);
        var started = _now;

        var (verdict, _) = UntilItActs(TimeSpan.FromMinutes(1));

        Assert.Equal(OutputStallVerdict.Reopen, verdict);
        Assert.InRange(_now - started, OutputStallWatch.StartGrace, OutputStallWatch.StartGrace + 2 * Check);
    }

    [Fact]
    public void APullBeforeTheStart_DoesNotCountAsAudioSinceIt()
    {
        // The device pulled long ago, was stopped for idleness, and started
        // again on a device that is gone: the old pull must not make it look
        // like a running device that just went quiet, nor hide the silence.
        StartAndPlay();
        _watch.Stopped();
        _now += TimeSpan.FromMinutes(10);
        _watch.Started(_now);
        var started = _now;

        var (verdict, _) = UntilItActs(TimeSpan.FromMinutes(1));

        Assert.Equal(OutputStallVerdict.Reopen, verdict);
        Assert.True(_now - started >= OutputStallWatch.StartGrace);
    }

    [Fact]
    public void OneStaleCheck_FollowedByAudio_IsNotAStall()
    {
        // The first check after the system wakes can find the last pull old;
        // the device pulls again before the next one.
        StartAndPlay();
        _now += TimeSpan.FromMinutes(30);
        Assert.Equal(OutputStallVerdict.Fine, _watch.Check(_now, _lastPull));

        Assert.Equal(OutputStallVerdict.Fine, Tick(pulling: true));
        Assert.Equal(OutputStallVerdict.Fine, Tick(pulling: true));
    }

    [Fact]
    public void AStoppedOutput_IsNeverReopened()
    {
        StartAndPlay();
        _watch.Stopped();

        Assert.Equal(OutputStallVerdict.Fine, UntilItActs(TimeSpan.FromHours(1)).Verdict);
    }

    [Fact]
    public void ANewStart_AfterReopening_WatchesTheNewDevice()
    {
        StartAndPlay();
        Assert.Equal(OutputStallVerdict.Reopen, UntilItActs(TimeSpan.FromMinutes(1)).Verdict);

        // The reopened device plays.
        _watch.Started(_now);
        for (var i = 0; i < 100; i++)
            Assert.Equal(OutputStallVerdict.Fine, Tick(pulling: true));
    }

    [Fact]
    public void ReopeningWithoutAudio_GivesUpAfterTheLimit_ThenWaitsForTheNextStart()
    {
        StartAndPlay();
        for (var i = 0; i < OutputStallWatch.MaxReopensWithoutAudio; i++)
        {
            Assert.Equal(OutputStallVerdict.Reopen, UntilItActs(TimeSpan.FromMinutes(1)).Verdict);
            _watch.Started(_now); // a device that opens but never plays
        }

        Assert.Equal(OutputStallVerdict.GiveUp, UntilItActs(TimeSpan.FromMinutes(1)).Verdict);
        // Given up: it says so once, not every few seconds.
        Assert.Equal(OutputStallVerdict.Fine, UntilItActs(TimeSpan.FromHours(1)).Verdict);

        // The next start (a play after the loss) gets its tries again.
        _watch.Started(_now);
        Assert.Equal(OutputStallVerdict.Reopen, UntilItActs(TimeSpan.FromMinutes(1)).Verdict);
    }

    [Fact]
    public void AudioBetweenReopenings_StartsTheCountAgain()
    {
        StartAndPlay();
        for (var round = 0; round < 3 * OutputStallWatch.MaxReopensWithoutAudio; round++)
        {
            Assert.Equal(OutputStallVerdict.Reopen, UntilItActs(TimeSpan.FromMinutes(1)).Verdict);
            // Each reopened device plays a while before it, too, goes quiet.
            StartAndPlay();
        }
    }
}
