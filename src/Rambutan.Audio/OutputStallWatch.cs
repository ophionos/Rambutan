namespace Rambutan.Audio;

/// <summary>What <see cref="OutputStallWatch.Check"/> asks the engine to do.</summary>
public enum OutputStallVerdict
{
    Fine,
    /// <summary>Open the output again by the usual rule.</summary>
    Reopen,
    /// <summary>Reopening brought no audio: call the output lost.</summary>
    GiveUp,
}

/// <summary>
/// Notices an output device that has stopped asking for audio. SoundFlow says
/// nothing when its device goes away: it registers no miniaudio notification,
/// raises <c>DeviceStopped</c> only for its own <c>Stop</c>, and its
/// <c>Start</c> ignores miniaudio's result. A device chosen by name that is
/// unplugged just stops pulling (PulseAudio and PipeWire end its stream, WASAPI
/// stops it, and starting it again does nothing), so the only sign is the
/// silence of the pull itself.
/// </summary>
/// <remarks>Times are from one monotonic clock. Not thread-safe; the engine
/// calls it under its device lock.</remarks>
public sealed class OutputStallWatch
{
    /// <summary>How long a device that has pulled since it started may go
    /// without pulling. A device pulls every period, tens of milliseconds
    /// (Bluetooth a few hundred).</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(2);

    /// <summary>How long a started device may take to pull at all: some take over
    /// a second (a PulseAudio null sink took 1.5 s).</summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(5);

    /// <summary>Reopenings in a row with no audio pulled in between before
    /// giving up.</summary>
    public const int MaxReopensWithoutAudio = 3;

    // The first check after the system wakes can find the last pull old before
    // the device pulls again, so one stale check is not enough.
    private const int StaleChecksToAct = 2;

    private bool _watching;
    private TimeSpan _startedAt;
    private int _staleChecks;
    private int _reopensWithoutAudio;

    /// <summary>The device started (or a reopened one did) at <paramref name="now"/>.
    /// After giving up, a start gets its tries again.</summary>
    public void Started(TimeSpan now)
    {
        if (!_watching && _reopensWithoutAudio > MaxReopensWithoutAudio)
            _reopensWithoutAudio = 0;
        _watching = true;
        _startedAt = now;
        _staleChecks = 0;
    }

    /// <summary>The device was stopped on purpose (nothing to play): its silence
    /// is expected.</summary>
    public void Stopped() => _watching = false;

    /// <param name="lastPull">When the device last asked for audio; null if never.</param>
    public OutputStallVerdict Check(TimeSpan now, TimeSpan? lastPull)
    {
        if (!_watching)
            return OutputStallVerdict.Fine;

        var pulledSinceStart = lastPull is { } pull && pull >= _startedAt;
        var silentFor = now - (pulledSinceStart ? lastPull!.Value : _startedAt);
        if (silentFor < (pulledSinceStart ? StallAfter : StartGrace))
        {
            _staleChecks = 0;
            if (pulledSinceStart)
                _reopensWithoutAudio = 0;
            return OutputStallVerdict.Fine;
        }

        if (++_staleChecks < StaleChecksToAct)
            return OutputStallVerdict.Fine;

        _staleChecks = 0;
        if (++_reopensWithoutAudio > MaxReopensWithoutAudio)
        {
            _watching = false;
            return OutputStallVerdict.GiveUp;
        }
        return OutputStallVerdict.Reopen;
    }
}
