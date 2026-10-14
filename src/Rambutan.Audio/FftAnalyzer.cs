using System.Numerics;

namespace Rambutan.Audio;

/// <summary>
/// Taps the mixed output and emits spectrum frames for the visualizer and
/// overlay, and a decimated stereo waveform for the styles that draw one, each
/// only while somebody reads it (see <see cref="ReadLease"/>). Fed on the
/// device callback; the FFT
/// (2048-point radix-2, Hann window) costs microseconds and allocates nothing
/// per frame, so it runs inline.
/// </summary>
public sealed class FftAnalyzer
{
    public const int WindowSize = 2048;
    public const int BandCount = 32;
    private const int HopSize = WindowSize / 2;
    private const float MinDb = -60f;

    /// <summary>Samples per channel in a waveform read: at <see cref="WaveformDecimation"/>
    /// that is ~43 ms, more than a frame at 30 fps, so consecutive frames overlap
    /// rather than leave gaps.</summary>
    public const int WaveformLength = 512;

    /// <summary>Device frames averaged into one waveform sample (48 kHz → 12 kHz).
    /// The average is a crude low-pass, enough for a drawn line.</summary>
    public const int WaveformDecimation = 4;

    private readonly float[] _window = new float[WindowSize];
    private readonly float[] _mono = new float[WindowSize];
    private readonly Complex[] _fft = new Complex[WindowSize];
    private readonly float[] _bands = new float[BandCount];
    private readonly float[] _bandsSnapshot = new float[BandCount];
    private readonly (int Start, int End)[] _bandBins;
    private int _filled;
    private long _version; // guarded by the snapshot lock

    /// <summary>
    /// How long the analysis keeps running after the last <see cref="CopyLatest"/>.
    /// Well above every reader's interval (a display frame; the overlay's 33 ms),
    /// short enough that a hidden visualizer stops costing within a moment.
    /// </summary>
    public static readonly TimeSpan ReadLease = TimeSpan.FromMilliseconds(500);

    private readonly Func<long> _clock;
    private long _lastReadAt = long.MinValue / 2; // milliseconds on _clock; lapsed until the first read
    private bool _idle = true; // device callback only

    // The waveform: a ring of decimated samples per channel, written by Feed
    // and read by CopyWaveform under _waveLock. Its lease is its own, so a
    // bars-only reader doesn't keep it running, nor a waveform reader the FFT.
    private readonly object _waveLock = new();
    private readonly float[] _waveLeft = new float[WaveformLength];
    private readonly float[] _waveRight = new float[WaveformLength];
    private int _waveHead; // next write; guarded by _waveLock
    private long _waveVersion; // guarded by _waveLock
    private long _lastWaveReadAt = long.MinValue / 2;
    private bool _waveIdle = true; // device callback only
    private float _sumLeft, _sumRight; // device callback only: the decimation in progress
    private int _summed;

    /// <param name="clock">Milliseconds, monotonic; <see cref="Environment.TickCount64"/>
    /// when null. Tests pass their own.</param>
    public FftAnalyzer(Func<long>? clock = null)
    {
        _clock = clock ?? (static () => Environment.TickCount64);
        for (var i = 0; i < WindowSize; i++)
            _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (WindowSize - 1)));
        _bandBins = BuildLogBands();
    }

    /// <summary>
    /// Latest spectrum frame, band magnitudes normalized 0..1. Copies into
    /// <paramref name="destination"/> and returns a version that increments per
    /// analyzed frame — an unchanged version means the feed has stopped (pause,
    /// device gone, or nobody read within <see cref="ReadLease"/>), letting
    /// consumers decay to silence instead of freezing.
    /// Each call also renews the lease that keeps the analysis running, so a
    /// reader must call at least once per lease. The first call after a lapse
    /// gets silence rather than the frame left over from before it.
    /// </summary>
    public long CopyLatest(Span<float> destination)
    {
        var now = _clock();
        lock (_bandsSnapshot)
        {
            if (now - Volatile.Read(ref _lastReadAt) > ReadLease.TotalMilliseconds)
                Array.Clear(_bandsSnapshot);
            Volatile.Write(ref _lastReadAt, now);
            _bandsSnapshot.AsSpan(0, Math.Min(BandCount, destination.Length)).CopyTo(destination);
            return _version;
        }
    }

    /// <summary>
    /// The latest <see cref="WaveformLength"/> decimated samples per channel,
    /// oldest first, -1..1, into <paramref name="left"/> and <paramref name="right"/>
    /// (the most recent ones that fit when they are shorter). Returns a version
    /// that increments with each fed block; an unchanged version means the feed
    /// has stopped. Renews the waveform's own lease, as
    /// <see cref="CopyLatest"/> does the bands'; the first read after a lapse
    /// gets silence.
    /// </summary>
    public long CopyWaveform(Span<float> left, Span<float> right)
    {
        var now = _clock();
        lock (_waveLock)
        {
            if (now - Volatile.Read(ref _lastWaveReadAt) > ReadLease.TotalMilliseconds)
            {
                Array.Clear(_waveLeft);
                Array.Clear(_waveRight);
            }
            Volatile.Write(ref _lastWaveReadAt, now);
            CopyRing(_waveLeft, left);
            CopyRing(_waveRight, right);
            return _waveVersion;
        }
    }

    private void CopyRing(float[] ring, Span<float> destination)
    {
        var count = Math.Min(WaveformLength, destination.Length);
        var start = (_waveHead - count + WaveformLength) % WaveformLength;
        var first = Math.Min(count, WaveformLength - start);
        ring.AsSpan(start, first).CopyTo(destination);
        ring.AsSpan(0, count - first).CopyTo(destination[first..]);
    }

    /// <summary>
    /// Feeds interleaved stereo samples from the device callback. Does nothing
    /// while nobody reads: the device keeps calling whether or not a visualizer,
    /// the meter or the OBS overlay is showing.
    /// </summary>
    public void Feed(ReadOnlySpan<float> interleaved)
    {
        var now = _clock();
        if (now - Volatile.Read(ref _lastReadAt) > ReadLease.TotalMilliseconds)
        {
            _idle = true;
        }
        else
        {
            if (_idle)
            {
                // Samples from before the lapse don't belong in the new first window.
                _idle = false;
                _filled = 0;
            }
            FeedBands(interleaved);
        }

        if (now - Volatile.Read(ref _lastWaveReadAt) > ReadLease.TotalMilliseconds)
        {
            _waveIdle = true;
        }
        else
        {
            if (_waveIdle)
            {
                _waveIdle = false;
                _summed = 0;
                _sumLeft = _sumRight = 0;
            }
            FeedWaveform(interleaved);
        }
    }

    private void FeedWaveform(ReadOnlySpan<float> interleaved)
    {
        lock (_waveLock)
        {
            for (var i = 0; i + 1 < interleaved.Length; i += FfmpegDecoder.Channels)
            {
                _sumLeft += interleaved[i];
                _sumRight += interleaved[i + 1];
                if (++_summed < WaveformDecimation)
                    continue;
                _waveLeft[_waveHead] = _sumLeft / WaveformDecimation;
                _waveRight[_waveHead] = _sumRight / WaveformDecimation;
                _waveHead = (_waveHead + 1) % WaveformLength;
                _summed = 0;
                _sumLeft = _sumRight = 0;
            }
            _waveVersion++;
        }
    }

    private void FeedBands(ReadOnlySpan<float> interleaved)
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
