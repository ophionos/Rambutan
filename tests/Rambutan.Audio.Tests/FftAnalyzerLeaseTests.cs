namespace Rambutan.Audio.Tests;

/// <summary>The analyzer runs only while a reader keeps renewing its lease.</summary>
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
}
