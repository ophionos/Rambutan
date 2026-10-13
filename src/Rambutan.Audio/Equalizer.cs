using System.Numerics;

namespace Rambutan.Audio;

/// <summary>
/// The 10-band graphic equalizer: one peaking biquad per ISO octave band,
/// run on the mixed output after the volume and before the FFT analyzer, so the
/// visualizer shows what is heard.
/// <para>
/// A boost lowers the overall level by the peak of the combined response, so the
/// curve can't push the output past full scale. That level is ramped per frame
/// like the volume, and turning the equalizer on or off crossfades over one
/// buffer. Off, or on and flat, is a bypass: the output is the input, bit for bit.
/// </para>
/// <para>
/// <see cref="Set"/> is called from any thread and publishes an immutable set of
/// coefficients; only the device thread, in <see cref="Process"/>, touches the
/// filter state. Coefficients and state are doubles: at 48 kHz the low bands'
/// poles sit close to the unit circle, where single precision is noisy.
/// </para>
/// </summary>
public sealed class Equalizer
{
    /// <summary>The band centres, in Hz: ISO octave bands.</summary>
    public static IReadOnlyList<int> Frequencies { get; } = [31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public const double MinGainDb = -12;
    public const double MaxGainDb = 12;

    private const int Channels = FfmpegDecoder.Channels;
    private const double SampleRate = FfmpegDecoder.SampleRate;

    // One octave per band: Q = √2 / (2 − 1).
    private static readonly double Q = Math.Sqrt(2);

    // As the mixer's volume: ~10 ms at 48 kHz, snapping once inaudibly close.
    private const float PreampApproachPerFrame = 0.002f;
    private const float PreampSnapDistance = 1e-4f;

    // State below this is inaudible; left alone it decays into subnormals, which
    // are slow, for as long as the input is silent (a pause sends zeros).
    private const double DenormalFloor = 1e-15;

    private Curve? _published;

    // Device thread only.
    private Curve? _applied;
    private readonly double[] _state = new double[Frequencies.Count * Channels * 2];
    private float _preamp = 1f;
    private float[] _dry = [];

    /// <summary>
    /// The equalizer's switch and its gains, one per band in <see cref="Frequencies"/>
    /// order, in dB. Gains are clamped to the range; NaN, or a list of the wrong
    /// length, is flat. Takes effect at the next buffer.
    /// </summary>
    public void Set(bool enabled, IReadOnlyList<double> gainsDb)
    {
        var gains = Normalize(gainsDb);
        Volatile.Write(ref _published, enabled ? Curve.For(gains) : null);
    }

    /// <summary>Gains in range, one per band: NaN is 0 dB, a list of the wrong length is flat.</summary>
    public static double[] Normalize(IReadOnlyList<double> gainsDb)
    {
        var gains = new double[Frequencies.Count];
        if (gainsDb.Count != gains.Length)
            return gains;
        for (var i = 0; i < gains.Length; i++)
            gains[i] = double.IsNaN(gainsDb[i]) ? 0 : Math.Clamp(gainsDb[i], MinGainDb, MaxGainDb);
        return gains;
    }

    /// <summary>
    /// Gains read back from storage, which keeps each band with its frequency so
    /// a later change of layout can't misread them. Anything that doesn't name
    /// exactly the bands of <see cref="Frequencies"/> is flat.
    /// </summary>
    public static double[] FromBands(IEnumerable<(int Hz, double Db)>? bands)
    {
        var list = bands?.ToList() ?? [];
        if (list.Count != Frequencies.Count || list.Select(b => b.Hz).Distinct().Count() != list.Count)
            return new double[Frequencies.Count];

        var gains = new double[Frequencies.Count];
        for (var i = 0; i < gains.Length; i++)
        {
            var match = list.FindIndex(b => b.Hz == Frequencies[i]);
            if (match < 0)
                return new double[Frequencies.Count];
            gains[i] = list[match].Db;
        }
        return Normalize(gains);
    }

    /// <summary>
    /// The level the curve gives up so it can't clip: minus the highest point of
    /// its combined response, in dB (0 for a curve that only cuts).
    /// </summary>
    public static double HeadroomDb(IReadOnlyList<double> gainsDb)
    {
        var curve = Curve.For(Normalize(gainsDb));
        return curve is null ? 0 : -curve.PeakDb;
    }

    /// <summary>The combined response of the curve at <paramref name="hz"/>, in dB, before the headroom.</summary>
    public static double ResponseDb(IReadOnlyList<double> gainsDb, double hz) =>
        Curve.For(Normalize(gainsDb))?.ResponseDb(hz) ?? 0;

    /// <summary>Equalizes interleaved stereo in place. Device thread only.</summary>
    public void Process(Span<float> buffer)
    {
        var next = Volatile.Read(ref _published);
        var previous = _applied;
        if (next is null && previous is null)
            return;

        if (ReferenceEquals(next, previous) || (next is not null && previous is not null))
        {
            // On, and staying on: new coefficients (if any) take over with the
            // state as it is, and the level ramps to the new headroom.
            _applied = next;
            if (!IsSilent(buffer))
                Filter(buffer, next!);
            return;
        }

        // Turning on or off: crossfade dry and equalized over this buffer.
        if (_dry.Length < buffer.Length)
            _dry = new float[buffer.Length];
        var dry = _dry.AsSpan(0, buffer.Length);
        buffer.CopyTo(dry);
        if (previous is null)
        {
            Array.Clear(_state);
            _preamp = next!.Preamp;
        }
        Filter(buffer, (next ?? previous)!);
        _applied = next;

        var frames = buffer.Length / Channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var t = (frame + 1) / (float)frames;
            var wet = next is null ? 1 - t : t;
            for (var ch = 0; ch < Channels; ch++)
            {
                var i = frame * Channels + ch;
                buffer[i] = buffer[i] * wet + dry[i] * (1 - wet);
            }
        }
    }

    /// <summary>Silent input with no ringing left: nothing to do, and nothing to decay.</summary>
    private bool IsSilent(ReadOnlySpan<float> buffer)
    {
        foreach (var sample in buffer)
        {
            if (sample != 0)
                return false;
        }
        foreach (var value in _state)
        {
            if (value != 0)
                return false;
        }
        return true;
    }

    private void Filter(Span<float> buffer, Curve curve)
    {
        var coefficients = curve.Coefficients;
        var bands = coefficients.Length / 5;
        var target = curve.Preamp;
        var frames = buffer.Length / Channels;

        for (var frame = 0; frame < frames; frame++)
        {
            if (_preamp != target)
            {
                _preamp += (target - _preamp) * PreampApproachPerFrame;
                if (Math.Abs(target - _preamp) < PreampSnapDistance)
                    _preamp = target;
            }

            for (var ch = 0; ch < Channels; ch++)
            {
                var i = frame * Channels + ch;
                double x = buffer[i];
                for (var band = 0; band < bands; band++)
                {
                    // Transposed direct form II: two state values per band and channel.
                    var c = band * 5;
                    var s = (band * Channels + ch) * 2;
                    var y = coefficients[c] * x + _state[s];
                    _state[s] = coefficients[c + 1] * x - coefficients[c + 3] * y + _state[s + 1];
                    _state[s + 1] = coefficients[c + 2] * x - coefficients[c + 4] * y;
                    x = y;
                }
                buffer[i] = (float)(x * _preamp);
            }
        }

        for (var i = 0; i < _state.Length; i++)
        {
            if (Math.Abs(_state[i]) < DenormalFloor)
                _state[i] = 0;
        }
    }

    /// <summary>
    /// A curve ready for the device thread: b0 b1 b2 a1 a2 (normalized by a0)
    /// for each band that isn't at 0 dB, and the level that keeps its peak at
    /// full scale. Null for a flat curve, which is the bypass.
    /// </summary>
    private sealed class Curve
    {
        private Curve(double[] coefficients, double peakDb)
        {
            Coefficients = coefficients;
            PeakDb = peakDb;
            Preamp = (float)Math.Pow(10, -peakDb / 20);
        }

        public double[] Coefficients { get; }

        /// <summary>The highest point of the response, in dB; at least 0.</summary>
        public double PeakDb { get; }

        public float Preamp { get; }

        public static Curve? For(double[] gains)
        {
            var coefficients = new List<double>();
            for (var band = 0; band < gains.Length; band++)
            {
                if (gains[band] != 0)
                    coefficients.AddRange(Peaking(Frequencies[band], gains[band]));
            }
            if (coefficients.Count == 0)
                return null;

            var shaped = new Curve([.. coefficients], 0);
            return new Curve(shaped.Coefficients, Math.Max(0, shaped.Peak()));
        }

        /// <summary>RBJ's peaking filter (Audio EQ Cookbook).</summary>
        private static double[] Peaking(double hz, double gainDb)
        {
            var a = Math.Pow(10, gainDb / 40);
            var w0 = 2 * Math.PI * hz / SampleRate;
            var alpha = Math.Sin(w0) / (2 * Q);
            var cos = Math.Cos(w0);
            var a0 = 1 + alpha / a;
            return
            [
                (1 + alpha * a) / a0,
                -2 * cos / a0,
                (1 - alpha * a) / a0,
                -2 * cos / a0,
                (1 - alpha / a) / a0,
            ];
        }

        public double ResponseDb(double hz)
        {
            var z = Complex.FromPolarCoordinates(1, -2 * Math.PI * hz / SampleRate);
            var z2 = z * z;
            var magnitude = 1.0;
            for (var c = 0; c < Coefficients.Length; c += 5)
            {
                var numerator = Coefficients[c] + Coefficients[c + 1] * z + Coefficients[c + 2] * z2;
                var denominator = 1 + Coefficients[c + 3] * z + Coefficients[c + 4] * z2;
                magnitude *= (numerator / denominator).Magnitude;
            }
            return 20 * Math.Log10(magnitude);
        }

        /// <summary>The highest point between 10 Hz and Nyquist, on a fine log grid.
        /// Neighbouring boosts overlap, so the peak can exceed the largest gain.</summary>
        private double Peak()
        {
            const int points = 1200;
            var low = Math.Log(10);
            var high = Math.Log(SampleRate / 2 * 0.999);
            var peak = double.NegativeInfinity;
            for (var i = 0; i <= points; i++)
                peak = Math.Max(peak, ResponseDb(Math.Exp(low + (high - low) * i / points)));
            return peak;
        }
    }
}
