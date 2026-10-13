using Rambutan.Core.Playback;

namespace Rambutan.Core.Tests;

/// <summary>The playback position between progress reports,
/// on a hand-driven clock.</summary>
public class PlaybackClockTests
{
    /// <summary>Monotonic time the test moves by hand.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private readonly ManualTime _time = new();

    private PlaybackClock Running(double at = 0)
    {
        var clock = new PlaybackClock(_time);
        clock.Snap(S(at));
        clock.SetRunning(true);
        return clock;
    }

    [Fact]
    public void Stopped_HoldsWhereItIs()
    {
        var clock = new PlaybackClock(_time);
        clock.Report(S(10));
        _time.Advance(5);

        Assert.False(clock.IsRunning);
        Assert.Equal(S(10), clock.Now);
    }

    [Fact]
    public void Running_FillsTheGapBetweenReports()
    {
        var clock = Running();
        clock.Report(S(10));

        _time.Advance(0.1);
        Assert.Equal(S(10.1), clock.Now);
        _time.Advance(0.15);
        Assert.Equal(S(10.25), clock.Now);
    }

    [Fact]
    public void Running_StopsAtTheRunAheadCap_WhenReportsStop()
    {
        var clock = Running();
        clock.Report(S(10));

        _time.Advance(3);

        Assert.Equal(S(10) + PlaybackClock.MaxRunAhead, clock.Now);
    }

    [Fact]
    public void AReportALittleBehind_HoldsUntilTheClockCatchesUp()
    {
        var clock = Running();
        clock.Report(S(10));
        _time.Advance(0.3);
        Assert.Equal(S(10.3), clock.Now);

        // The engine says 10.2: shown time doesn't step back to it.
        clock.Report(S(10.2));
        Assert.Equal(S(10.3), clock.Now);
        _time.Advance(0.05);
        Assert.Equal(S(10.3), clock.Now);
        _time.Advance(0.1);
        Assert.Equal(S(10.35), clock.Now);
    }

    [Fact]
    public void AReportFarBehind_IsAJump_AndIsFollowed()
    {
        var clock = Running();
        clock.Report(S(60));

        clock.Report(S(12));

        Assert.Equal(S(12), clock.Now);
    }

    [Fact]
    public void AReportAhead_MovesTheClockForward()
    {
        var clock = Running();
        clock.Report(S(10));

        clock.Report(S(40));

        Assert.Equal(S(40), clock.Now);
    }

    [Fact]
    public void Pausing_FreezesWhereItWas_AndResumingRunsOnFromThere()
    {
        var clock = Running();
        clock.Report(S(10));
        _time.Advance(0.2);

        clock.SetRunning(false);
        _time.Advance(30);
        Assert.Equal(S(10.2), clock.Now);

        clock.SetRunning(true);
        _time.Advance(0.1);
        Assert.Equal(S(10.3), clock.Now);
    }

    [Fact]
    public void Snap_MovesAtOnce_EvenBackwards()
    {
        var clock = Running();
        clock.Report(S(10));
        _time.Advance(0.2);

        clock.Snap(S(9.8));

        Assert.Equal(S(9.8), clock.Now);
        _time.Advance(0.1);
        Assert.Equal(S(9.9), clock.Now);
    }

    [Fact]
    public void ANewTrack_StartsFromZero()
    {
        var clock = Running();
        clock.Report(S(200));

        clock.Snap(TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, clock.Now);
    }

    [Fact]
    public void Now_NeverGoesBackwards_AcrossManyReads()
    {
        var clock = Running();
        var last = TimeSpan.Zero;
        double[] reports = [0.25, 0.48, 0.77, 0.99, 1.26, 1.49];
        foreach (var report in reports)
        {
            for (var i = 0; i < 4; i++)
            {
                _time.Advance(0.0625);
                var now = clock.Now;
                Assert.True(now >= last, $"{now} after {last}");
                last = now;
            }
            clock.Report(S(report));
        }
    }
}
