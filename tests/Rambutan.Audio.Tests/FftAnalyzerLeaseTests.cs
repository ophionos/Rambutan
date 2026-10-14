namespace Rambutan.Audio.Tests;

/// <summary>The analyzer runs only while a reader keeps renewing its lease, and
/// the bands and the waveform each have their own.</summary>
public class FftAnalyzerLeaseTests
{
    private const int Rate = FfmpegDecoder.SampleRate;

    private long _now = 1_000_000;

    private FftAnalyzer Create() => new(() => _now);

    private static long Lease => (long)FftAnalyzer.ReadLease.TotalMilliseconds;

    /// <summary>Interleaved stereo sine, loud enough to light its band.</summary>
    private static float[] Tone(int windows, double hz = 1000)
    {
        var frames = FftAnalyzer.WindowSize * windows;
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
            samples[2 * i] = samples[2 * i + 1] = 0.5f * (float)Math.Sin(2 * Math.PI * hz * i / Rate);
        return samples;
    }

    private static float[] Bands() => new float[FftAnalyzer.BandCount];

    private static float[] Wave() => new float[FftAnalyzer.WaveformLength];

    /// <summary>Interleaved stereo: a sine on the left, silence on the right.</summary>
    private static float[] LeftOnly(int frames)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
            samples[2 * i] = 0.5f * (float)Math.Sin(2 * Math.PI * 200 * i / Rate);
        return samples;
    }

    private static float Loudest(float[] wave) => wave.Max(Math.Abs);

    [Fact]
    public void WithoutAReader_NothingIsAnalyzed()
    {
        var analyzer = Create();
        analyzer.Feed(Tone(4));

        var bands = Bands();
        Assert.Equal(0, analyzer.CopyLatest(bands));
        Assert.All(bands, b => Assert.Equal(0f, b));
    }

    [Fact]
    public void AReader_StartsTheAnalysis()
    {
        var analyzer = Create();
        var bands = Bands();
        var before = analyzer.CopyLatest(bands);

        analyzer.Feed(Tone(4));

        Assert.True(analyzer.CopyLatest(bands) > before);
        Assert.Contains(bands, b => b > 0.5f);
    }

    [Fact]
    public void TheLease_HoldsUntilItsEnd()
    {
        var analyzer = Create();
        var bands = Bands();
        var before = analyzer.CopyLatest(bands);

        _now += Lease;
        analyzer.Feed(Tone(2));

        Assert.True(analyzer.CopyLatest(bands) > before);
    }

    [Fact]
    public void TheLease_Lapses_WhenNobodyReads()
    {
        var analyzer = Create();
        var bands = Bands();
        analyzer.CopyLatest(bands);
        analyzer.Feed(Tone(2));
        var version = analyzer.CopyLatest(bands);

        _now += Lease + 1;
        analyzer.Feed(Tone(4));

        // The read after the lapse sees no new frame, and renews the lease.
        Assert.Equal(version, analyzer.CopyLatest(bands));
    }

    [Fact]
    public void TheFirstReadAfterALapse_GetsSilence_NotTheOldFrame()
    {
        var analyzer = Create();
        var bands = Bands();
        analyzer.CopyLatest(bands);
        analyzer.Feed(Tone(2));
        analyzer.CopyLatest(bands);
        Assert.Contains(bands, b => b > 0.5f);

        _now += Lease + 1;
        analyzer.CopyLatest(bands);

        Assert.All(bands, b => Assert.Equal(0f, b));
    }

    [Fact]
    public void AfterALapse_SamplesFromBeforeIt_StayOutOfTheNextWindow()
    {
        // Half a window of loud tone, then the lease lapses mid-window. Fed
        // silence afterwards, the next window must be silent: the tone's half
        // window was dropped, not completed by the silence.
        var analyzer = Create();
        var bands = Bands();
        analyzer.CopyLatest(bands);
        analyzer.Feed(Tone(1).AsSpan(0, FftAnalyzer.WindowSize).ToArray());

        _now += Lease + 1;
        analyzer.Feed(new float[256]);
        analyzer.CopyLatest(bands);
        analyzer.Feed(new float[FftAnalyzer.WindowSize * 2]);

        var version = analyzer.CopyLatest(bands);
        Assert.Equal(1, version);
        Assert.All(bands, b => Assert.True(b < 0.01f));
    }

    [Fact]
    public void WithoutAWaveformReader_NoWaveformIsKept()
    {
        var analyzer = Create();
        analyzer.Feed(Tone(2));

        var left = Wave();
        var right = Wave();
        Assert.Equal(0, analyzer.CopyWaveform(left, right));
        Assert.Equal(0f, Loudest(left));
    }

    [Fact]
    public void AWaveformReader_GetsTheRecentSamples()
    {
        var analyzer = Create();
        var left = Wave();
        var right = Wave();
        var before = analyzer.CopyWaveform(left, right);

        analyzer.Feed(Tone(1));

        Assert.True(analyzer.CopyWaveform(left, right) > before);
        // The decimating average takes a little off a 1 kHz peak of 0.5.
        Assert.InRange(Loudest(left), 0.4f, 0.5f);
        Assert.InRange(Loudest(right), 0.4f, 0.5f);
    }

    [Fact]
    public void TheWaveform_KeepsTheChannelsApart()
    {
        var analyzer = Create();
        var left = Wave();
        var right = Wave();
        analyzer.CopyWaveform(left, right);

        analyzer.Feed(LeftOnly(FftAnalyzer.WindowSize));
        analyzer.CopyWaveform(left, right);

        Assert.True(Loudest(left) > 0.4f);
        Assert.Equal(0f, Loudest(right));
    }

    [Fact]
    public void TheWaveform_IsOldestFirst()
    {
        // Silence, then exactly one waveform's worth of a rising ramp: every
        // sample read is ramp, from its first step to its last.
        var analyzer = Create();
        var left = Wave();
        var right = Wave();
        analyzer.CopyWaveform(left, right);

        analyzer.Feed(new float[FftAnalyzer.WaveformLength * FftAnalyzer.WaveformDecimation * 2]);
        var ramp = new float[FftAnalyzer.WaveformLength * FftAnalyzer.WaveformDecimation * 2];
        for (var i = 0; i < ramp.Length / 2; i++)
            ramp[2 * i] = ramp[2 * i + 1] = (i / FftAnalyzer.WaveformDecimation + 1) / (float)FftAnalyzer.WaveformLength;
        analyzer.Feed(ramp);
        analyzer.CopyWaveform(left, right);

        Assert.Equal(1f / FftAnalyzer.WaveformLength, left[0], 1e-6f);
        Assert.Equal(1f, left[^1], 1e-6f);
        for (var i = 1; i < left.Length; i++)
            Assert.True(left[i] > left[i - 1]);
    }

    [Fact]
    public void AShorterDestination_GetsTheMostRecentSamples()
    {
        var analyzer = Create();
        var whole = Wave();
        var right = Wave();
        analyzer.CopyWaveform(whole, right);
        analyzer.Feed(Tone(1, hz: 300));
        analyzer.CopyWaveform(whole, right);

        var tail = new float[100];
        analyzer.CopyWaveform(tail, new float[100]);

        Assert.Equal(whole[^100..], tail);
    }

    [Fact]
    public void TheWaveformLease_Lapses_AndTheFirstReadAfterItGetsSilence()
    {
        var analyzer = Create();
        var left = Wave();
        var right = Wave();
        analyzer.CopyWaveform(left, right);
        analyzer.Feed(Tone(1));
        var version = analyzer.CopyWaveform(left, right);

        _now += Lease + 1;
        analyzer.Feed(Tone(1));

        Assert.Equal(version, analyzer.CopyWaveform(left, right));
        Assert.Equal(0f, Loudest(left));
    }

    [Fact]
    public void ReadingTheBands_DoesNotKeepTheWaveformRunning()
    {
        var analyzer = Create();
        var bands = Bands();
        analyzer.CopyLatest(bands);

        analyzer.Feed(Tone(2));

        Assert.True(analyzer.CopyLatest(bands) > 0);
        Assert.Equal(0, analyzer.CopyWaveform(Wave(), Wave()));
    }

    [Fact]
    public void ReadingTheWaveform_DoesNotKeepTheBandsRunning()
    {
        var analyzer = Create();
        analyzer.CopyWaveform(Wave(), Wave());

        analyzer.Feed(Tone(4));

        Assert.True(analyzer.CopyWaveform(Wave(), Wave()) > 0);
        var bands = Bands();
        Assert.Equal(0, analyzer.CopyLatest(bands));
        Assert.All(bands, b => Assert.Equal(0f, b));
    }
}
