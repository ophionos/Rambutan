namespace Rambutan.Audio.Tests;

/// <summary>When the output stops for having nothing to play.</summary>
public class OutputIdleTimerTests
{
    private static readonly TimeSpan Delay = OutputIdleTimer.DefaultDelay;
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private readonly ManualTime _time = new();
    private int _stops;

    private OutputIdleTimer Create() => new(() => _stops++, time: _time);

    [Fact]
    public void TheDelay_IsTenMinutes() =>
        Assert.Equal(TimeSpan.FromMinutes(10), OutputIdleTimer.DefaultDelay);

    [Fact]
    public void Armed_ItStopsOnce_WhenTheDelayPasses()
    {
        using var idle = Create();
        idle.Arm();

        _time.Advance(Delay - Second);
        Assert.Equal(0, _stops);

        _time.Advance(Second);
        Assert.Equal(1, _stops);

        _time.Advance(Delay * 3);
        Assert.Equal(1, _stops);
    }

    [Fact]
    public void AResumeWithinTheDelay_KeepsTheOutput()
    {
        using var idle = Create();
        idle.Arm();
        _time.Advance(Delay - Second);
        idle.Disarm();

        _time.Advance(Delay * 2);
        Assert.Equal(0, _stops);
    }

    [Fact]
    public void ASecondPause_DoesNotPutTheDeadlineBack()
    {
        using var idle = Create();
        idle.Arm();
        _time.Advance(Delay / 2);
        idle.Arm();

        _time.Advance(Delay / 2);
        Assert.Equal(1, _stops);
    }

    [Fact]
    public void APauseAfterAResume_WaitsTheWholeDelayAgain()
    {
        using var idle = Create();
        idle.Arm();
        _time.Advance(Delay / 2);
        idle.Disarm();
        idle.Arm();

        _time.Advance(Delay - Second);
        Assert.Equal(0, _stops);
        _time.Advance(Second);
        Assert.Equal(1, _stops);
    }

    [Fact]
    public void AfterAStop_TheNextPause_StopsAgain()
    {
        using var idle = Create();
        idle.Arm();
        _time.Advance(Delay);
        idle.Disarm();
        idle.Arm();
        _time.Advance(Delay);

        Assert.Equal(2, _stops);
    }

    [Fact]
    public void NeverArmed_ItNeverStops()
    {
        using var idle = Create();
        _time.Advance(Delay * 5);
        Assert.Equal(0, _stops);
    }

    /// <summary>A clock that moves, and fires its one-shot timers, only when told.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by)
        {
            _now += by;
            var due = _timers.Where(t => t.DueAt is { } at && at <= _now).ToArray();
            foreach (var timer in due)
            {
                timer.DueAt = null;
                timer.Fire();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        private sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? DueAt { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan)
                    throw new NotSupportedException("Only one-shot timers.");
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : time._now + dueTime;
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                DueAt = null;
                time._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
