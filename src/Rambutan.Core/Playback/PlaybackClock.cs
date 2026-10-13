namespace Rambutan.Core.Playback;

/// <summary>
/// The playback position between progress reports. The engine
/// reports about every 250 ms: enough for a line highlight, not for a karaoke
/// sweep, which reads <see cref="Now"/> on every frame. Between reports the clock
/// runs on from the last one at real speed while playback advances.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>It runs only while <see cref="SetRunning"/> says playback advances, and
/// freezes where it is otherwise (paused, buffering, loading).</item>
/// <item>It never runs backwards between reports: a report a little behind what
/// it already showed holds it until the extrapolation catches up. A report more
/// than <see cref="JumpThreshold"/> behind is a jump, and it follows.</item>
/// <item>It runs at most <see cref="MaxRunAhead"/> past the last report, so a
/// stalled engine doesn't carry the sweep on alone.</item>
/// <item><see cref="Snap"/> moves it at once (a seek, a new track).</item>
/// </list>
/// Thread-safe: reports come from the engine's threads, reads from the render loop.
/// </remarks>
public sealed class PlaybackClock
{
    /// <summary>Two report periods: one missed report is normal, two is a stall.</summary>
    public static readonly TimeSpan MaxRunAhead = TimeSpan.FromMilliseconds(500);

    /// <summary>How far behind the clock a report must be to count as a jump rather than drift.</summary>
    public static readonly TimeSpan JumpThreshold = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private TimeSpan _anchor;        // position at _anchorStamp
    private long _anchorStamp;
    private TimeSpan _reported;      // the last report, for the run-ahead cap
    private TimeSpan _shown;         // the latest value Now returned
    private bool _running;

    public PlaybackClock(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _anchorStamp = _time.GetTimestamp();
    }

    /// <summary>The position now: extrapolated, capped, and never behind what was last shown.</summary>
    public TimeSpan Now
    {
        get
        {
            lock (_gate)
            {
                var value = Extrapolate();
                if (value > _shown)
                    _shown = value;
                return _shown;
            }
        }
    }

    /// <summary>Whether playback advances: true while playing, false while paused,
    /// buffering, loading or stopped.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _running;
        }
    }

    public void Report(TimeSpan position)
    {
        lock (_gate)
        {
            if (position < _shown - JumpThreshold)
                _shown = position;

            _reported = position;
            Anchor(position);
        }
    }

    public void SetRunning(bool running)
    {
        lock (_gate)
        {
            if (running == _running)
                return;

            // Re-anchor at what is on screen, so neither edge makes it jump.
            var here = Extrapolate();
            if (here < _shown)
                here = _shown;
            _running = running;
            Anchor(here);
        }
    }

    /// <summary>Moves the clock to <paramref name="position"/> at once, backwards
    /// included: a seek, or a new track (at zero).</summary>
    public void Snap(TimeSpan position)
    {
        lock (_gate)
        {
            _shown = position;
            _reported = position;
            Anchor(position);
        }
    }

    private void Anchor(TimeSpan position)
    {
        _anchor = position;
        _anchorStamp = _time.GetTimestamp();
    }

    private TimeSpan Extrapolate()
    {
        if (!_running)
            return _anchor;

        var value = _anchor + _time.GetElapsedTime(_anchorStamp);
        var cap = _reported + MaxRunAhead;
        return value > cap ? (cap > _anchor ? cap : _anchor) : value;
    }
}
