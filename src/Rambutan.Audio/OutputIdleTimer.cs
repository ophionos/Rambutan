namespace Rambutan.Audio;

/// <summary>
/// Runs an action once the output has had nothing to play for <see cref="Delay"/>:
/// armed on pause or stop, disarmed on play or resume. A running output device
/// keeps calling back every period even when it only plays silence, which costs
/// CPU and wakeups for as long as the app sits paused. The delay keeps a quick
/// pause and resume instant, because starting a device again takes time
/// (Bluetooth output can take long enough to lose the start of a track).
/// </summary>
public sealed class OutputIdleTimer : IDisposable
{
    /// <summary>The maintainer's choice: long pauses save, short ones stay instant.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMinutes(10);

    private readonly Lock _lock = new();
    private readonly Action _onIdle;
    private readonly ITimer _timer;
    private bool _armed;

    /// <param name="onIdle">Runs on a timer thread, under this timer's lock, so a
    /// <see cref="Disarm"/> racing it returns only after it is done.</param>
    public OutputIdleTimer(Action onIdle, TimeSpan? delay = null, TimeProvider? time = null)
    {
        _onIdle = onIdle;
        Delay = delay ?? DefaultDelay;
        _timer = (time ?? TimeProvider.System).CreateTimer(
            _ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TimeSpan Delay { get; }

    /// <summary>Starts the wait, unless it is already running: a second pause
    /// doesn't put the deadline back.</summary>
    public void Arm()
    {
        lock (_lock)
        {
            if (_armed)
                return;
            _armed = true;
            _timer.Change(Delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Disarm()
    {
        lock (_lock)
        {
            _armed = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_lock)
        {
            // A disarm can land between the timer coming due and this lock.
            if (!_armed)
                return;
            _armed = false;
            _onIdle();
        }
    }

    public void Dispose()
    {
        lock (_lock)
            _armed = false;
        _timer.Dispose();
    }
}
