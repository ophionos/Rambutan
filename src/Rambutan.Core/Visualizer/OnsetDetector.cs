namespace Rambutan.Core.Visualizer;

/// <summary>
/// Finds beats in the analyzer's band frames: spectral flux (how much the low
/// bands rose since the previous frame) against an adaptive threshold (the
/// recent flux's mean plus <see cref="Sensitivity"/> standard deviations), so a
/// quiet passage's kicks count as well as a loud one's. A slow swell rises
/// evenly and so doesn't cross its own average.
/// </summary>
/// <remarks>
/// Beats are at least <see cref="MinInterval"/> apart. That is a cap on what
/// the visualizer may flash, not only a debounce: large-area luminance flashes
/// must stay under three a second (WCAG 2.3.1), and styles pulse on these beats.
/// Feed it each new analyzer frame once (a repeated frame has no flux and would
/// skew the threshold). One thread; allocates nothing after construction.
/// </remarks>
public sealed class OnsetDetector
{
    /// <summary>Above three a second's 333 ms, with room for jitter in frame times.</summary>
    public const double MinInterval = 0.35;

    /// <summary>How much recent flux the threshold is taken from, in seconds.</summary>
    public const double HistorySeconds = 1.0;

    /// <summary>Standard deviations above the recent mean a frame's flux must reach.</summary>
    public const double Sensitivity = 1.5;

    /// <summary>
    /// The least flux that counts, averaged per band on the analyzer's 0..1 scale
    /// (60 dB across): 3 dB of rise across the low bands. Keeps noise and a
    /// steady signal's flicker from counting where the recent flux is near zero.
    /// </summary>
    public const double MinFlux = 0.05;

    /// <summary>How fast <see cref="Pulse"/> falls after a beat, in seconds.</summary>
    public const double PulseDecay = 0.15;

    // At the analyzer's ~47 frames a second, a second of history fits with room.
    private const int HistoryCapacity = 96;

    private readonly int _lowBands;
    private readonly float[] _previous;
    private readonly double[] _historyTime = new double[HistoryCapacity];
    private readonly double[] _historyFlux = new double[HistoryCapacity];
    private int _historyHead;
    private int _historyCount;
    private bool _hasPrevious;

    /// <param name="lowBands">How many of the lowest bands carry the beat: the
    /// kick and bass. The analyzer's 32 log bands from 50 Hz put 8 under ~210 Hz.</param>
    public OnsetDetector(int lowBands = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lowBands, 1);
        _lowBands = lowBands;
        _previous = new float[lowBands];
    }

    /// <summary>When the last beat was, in the caller's seconds; negative
    /// infinity before the first.</summary>
    public double LastBeatAt { get; private set; } = double.NegativeInfinity;

    /// <summary>The last beat's strength, 0..1: its flux against the threshold
    /// it crossed, 1 at twice the threshold or more.</summary>
    public float LastStrength { get; private set; }

    /// <summary>
    /// One analyzer frame at <paramref name="time"/> (seconds, any monotonic
    /// origin). True when it is a beat. <paramref name="bands"/> needs at least
    /// the low bands given to the constructor.
    /// </summary>
    public bool Update(ReadOnlySpan<float> bands, double time)
    {
        var flux = 0.0;
        for (var i = 0; i < _lowBands; i++)
        {
            if (_hasPrevious)
                flux += Math.Max(0, bands[i] - _previous[i]);
            _previous[i] = bands[i];
        }
        if (!_hasPrevious)
        {
            _hasPrevious = true;
            return false;
        }
        flux /= _lowBands;

        var threshold = Threshold(time);
        Remember(time, flux);

        if (flux < threshold || time - LastBeatAt < MinInterval)
            return false;

        LastBeatAt = time;
        LastStrength = (float)Math.Min(1, flux / (2 * threshold));
        return true;
    }

    /// <summary>The beat envelope at <paramref name="time"/>: the last beat's
    /// strength, falling exponentially. 0 before the first beat.</summary>
    public float Pulse(double time)
    {
        var since = time - LastBeatAt;
        return since < 0 || double.IsInfinity(since)
            ? 0f
            : LastStrength * (float)Math.Exp(-since / PulseDecay);
    }

    /// <summary>Forgets the history (a new track, the feed restarting).</summary>
    public void Reset()
    {
        _hasPrevious = false;
        _historyCount = 0;
        _historyHead = 0;
        LastBeatAt = double.NegativeInfinity;
        LastStrength = 0;
    }

    private double Threshold(double time)
    {
        var count = 0;
        double sum = 0, sumSquares = 0;
        for (var i = 0; i < _historyCount; i++)
        {
            var index = (_historyHead - 1 - i + HistoryCapacity) % HistoryCapacity;
            if (time - _historyTime[index] > HistorySeconds)
                break;
            sum += _historyFlux[index];
            sumSquares += _historyFlux[index] * _historyFlux[index];
            count++;
        }
        if (count == 0)
            return MinFlux;

        var mean = sum / count;
        var deviation = Math.Sqrt(Math.Max(0, sumSquares / count - mean * mean));
        return mean + Sensitivity * deviation + MinFlux;
    }

    private void Remember(double time, double flux)
    {
        _historyTime[_historyHead] = time;
        _historyFlux[_historyHead] = flux;
        _historyHead = (_historyHead + 1) % HistoryCapacity;
        _historyCount = Math.Min(_historyCount + 1, HistoryCapacity);
    }
}
