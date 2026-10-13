using System.Numerics;

namespace Rambutan.Audio;

/// <summary>
/// Taps the mixed output and continuously emits spectrum frames for the
/// visualizer and overlay. Fed on the device callback; the FFT
/// (2048-point radix-2, Hann window) costs microseconds and allocates nothing
/// per frame, so it runs inline.
/// </summary>
public sealed class FftAnalyzer
{
    public const int WindowSize = 2048;
    public const int BandCount = 32;
    private const int HopSize = WindowSize / 2;
    private const float MinDb = -60f;

    private readonly float[] _window = new float[WindowSize];
    private readonly float[] _mono = new float[WindowSize];
    private readonly Complex[] _fft = new Complex[WindowSize];
    private readonly float[] _bands = new float[BandCount];
    private readonly float[] _bandsSnapshot = new float[BandCount];
    private readonly (int Start, int End)[] _bandBins;
    private int _filled;
    private long _version; // guarded by the snapshot lock

    public FftAnalyzer()
    {
        for (var i = 0; i < WindowSize; i++)
            _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (WindowSize - 1)));
        _bandBins = BuildLogBands();
    }

    /// <summary>
    /// Latest spectrum frame, band magnitudes normalized 0..1. Copies into
    /// <paramref name="destination"/> and returns a version that increments per
    /// analyzed frame — an unchanged version means the feed has stopped (pause,
    /// device gone), letting consumers decay to silence instead of freezing.
    /// </summary>
    public long CopyLatest(Span<float> destination)
    {
        lock (_bandsSnapshot)
        {
            _bandsSnapshot.AsSpan(0, Math.Min(BandCount, destination.Length)).CopyTo(destination);
            return _version;
        }
    }

    /// <summary>Feeds interleaved stereo samples from the device callback.</summary>
    public void Feed(ReadOnlySpan<float> interleaved)
    {
        for (var i = 0; i + 1 < interleaved.Length; i += FfmpegDecoder.Channels)
        {
            _mono[_filled++] = (interleaved[i] + interleaved[i + 1]) * 0.5f;
            if (_filled == WindowSize)
            {
                Analyze();
                // 50% overlap: slide the second half down and continue.
                Array.Copy(_mono, HopSize, _mono, 0, HopSize);
                _filled = HopSize;
            }
        }
    }

    private void Analyze()
    {
        for (var i = 0; i < WindowSize; i++)
            _fft[i] = new Complex(_mono[i] * _window[i], 0);

        Transform(_fft);

        for (var band = 0; band < BandCount; band++)
        {
            var (start, end) = _bandBins[band];
            var peak = 0.0;
            for (var bin = start; bin < end; bin++)
            {
                var magnitude = _fft[bin].Magnitude / (WindowSize / 4.0);
                if (magnitude > peak)
                    peak = magnitude;
            }

            var db = 20.0 * Math.Log10(Math.Max(peak, 1e-9));
            _bands[band] = Math.Clamp((float)((db - MinDb) / -MinDb), 0f, 1f);
        }

        lock (_bandsSnapshot)
        {
            _bands.CopyTo(_bandsSnapshot, 0);
            _version++;
        }
    }

    /// <summary>In-place iterative radix-2 Cooley–Tukey.</summary>
    private static void Transform(Complex[] data)
    {
        var n = data.Length;

        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j |= bit;
            if (i < j)
                (data[i], data[j]) = (data[j], data[i]);
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var wBase = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += length)
            {
                var w = Complex.One;
                for (var k = 0; k < length / 2; k++)
                {
                    var even = data[i + k];
                    var odd = data[i + k + length / 2] * w;
                    data[i + k] = even + odd;
                    data[i + k + length / 2] = even - odd;
                    w *= wBase;
                }
            }
        }
    }

    /// <summary>Log-spaced bands from ~50 Hz to ~16 kHz over the usable bins.</summary>
    private static (int, int)[] BuildLogBands()
    {
        const double loHz = 50, hiHz = 16000;
        var hzPerBin = (double)FfmpegDecoder.SampleRate / WindowSize;
        var bands = new (int, int)[BandCount];

        for (var band = 0; band < BandCount; band++)
        {
            var lo = loHz * Math.Pow(hiHz / loHz, band / (double)BandCount);
            var hi = loHz * Math.Pow(hiHz / loHz, (band + 1) / (double)BandCount);
            var startBin = Math.Max(1, (int)(lo / hzPerBin));
            var endBin = Math.Min(WindowSize / 2, Math.Max(startBin + 1, (int)Math.Ceiling(hi / hzPerBin)));
            bands[band] = (startBin, endBin);
        }

        return bands;
    }
}
