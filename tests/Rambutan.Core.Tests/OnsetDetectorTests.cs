using Rambutan.Core.Visualizer;

namespace Rambutan.Core.Tests;

/// <summary>Beats from band frames: kicks count at any loudness, swells and steady
/// sound don't, and beats never come faster than the flash cap.</summary>
public class OnsetDetectorTests
{
    private const int Bands = 32;

    /// <summary>The analyzer's frame period: a 1024-sample hop at 48 kHz.</summary>
    private const double Frame = 1024.0 / 48000;

    /// <summary>
    /// Feeds <paramref name="seconds"/> of frames from <paramref name="start"/>,
    /// with the low bands at <paramref name="level"/>(t), and returns the beat times.
    /// </summary>
    private static List<double> Run(OnsetDetector detector, double start, double seconds, Func<double, float> level)
    {
        var beats = new List<double>();
        var bands = new float[Bands];
        for (var t = start; t < start + seconds; t += Frame)
        {
            Array.Fill(bands, level(t), 0, 8);
            Array.Fill(bands, 0.3f, 8, Bands - 8);
            if (detector.Update(bands, t))
                beats.Add(t);
        }
        return beats;
    }

    /// <summary>A kick every <paramref name="period"/> seconds: the low bands jump
    /// to <paramref name="floor"/> + <paramref name="height"/> and fall back over ~100 ms.</summary>
    private static Func<double, float> Kicks(double period, float height, float floor = 0.3f) =>
        t => floor + height * (float)Math.Exp(-(t % period) / 0.04);

    [Fact]
    public void ASteadyLevel_HasNoBeats()
    {
        var detector = new OnsetDetector();
        Assert.Empty(Run(detector, 0, 5, _ => 0.6f).Skip(1));
    }

    [Fact]
    public void Silence_HasNoBeats()
    {
        var detector = new OnsetDetector();
        Assert.Empty(Run(detector, 0, 5, _ => 0f));
    }

    [Fact]
    public void LoudKicks_AreFound_OncePerKick()
    {
        var detector = new OnsetDetector();
        var beats = Run(detector, 0, 4, Kicks(0.5, 0.5f));

        // Two a second for four seconds; the first kick is in the first frame,
        // which has no frame before it to rise from.
        Assert.InRange(beats.Count, 7, 8);
        for (var i = 1; i < beats.Count; i++)
            Assert.Equal(0.5, beats[i] - beats[i - 1], 0.03);
    }

    [Fact]
    public void QuietKicks_AreFound()
    {
        var detector = new OnsetDetector();
        var beats = Run(detector, 0, 4, Kicks(0.5, 0.15f, floor: 0.1f));
        Assert.InRange(beats.Count, 7, 8);
    }

    [Fact]
    public void AfterALoudPassage_TheThreshold_ComesDownToQuietKicks()
    {
        var detector = new OnsetDetector();
        Run(detector, 0, 4, Kicks(0.5, 0.6f));

        var beats = Run(detector, 4, 4, Kicks(0.5, 0.15f, floor: 0.1f));

        // Within the history's second the quiet kicks count again.
        Assert.NotEmpty(beats);
        Assert.True(beats[0] < 4 + OnsetDetector.HistorySeconds + 0.5);
        Assert.True(beats.Count >= 5);
    }

    [Fact]
    public void ASlowSwell_HasNoBeats()
    {
        // From silence to full over four seconds: a steady rise, no beat.
        var detector = new OnsetDetector();
        Assert.Empty(Run(detector, 0, 4, t => (float)(t / 4)));
    }

    [Fact]
    public void BeatsFasterThanTheCap_AreThinnedToIt()
    {
        // Eight kicks a second, a strobe if every one flashed.
        var detector = new OnsetDetector();
        var beats = Run(detector, 0, 4, Kicks(0.125, 0.5f));

        Assert.NotEmpty(beats);
        for (var i = 1; i < beats.Count; i++)
            Assert.True(beats[i] - beats[i - 1] >= OnsetDetector.MinInterval);
        Assert.True(beats.Count <= 4 / OnsetDetector.MinInterval + 1);
    }

    [Fact]
    public void MusicAfterSilence_StartsWithOneBeat_NotABurst()
    {
        var detector = new OnsetDetector();
        Run(detector, 0, 2, _ => 0f);

        // Loud, busy music resumes: the jump in is a beat, and the frames right
        // after it are not.
        var beats = Run(detector, 2, 0.34, t => 0.7f + 0.2f * (float)Math.Sin(t * 40));
        Assert.Single(beats);
    }

    [Fact]
    public void ThePulse_FallsAfterABeat()
    {
        var detector = new OnsetDetector();
        Assert.Equal(0f, detector.Pulse(0));

        // The kick at 0 is the first frame and has nothing to rise from; the one at 1 counts.
        var beat = Run(detector, 0, 1.2, Kicks(1, 0.5f)).Single();

        var at = detector.Pulse(beat);
        Assert.True(at > 0.5f);
        Assert.True(detector.Pulse(beat + OnsetDetector.PulseDecay) < at * 0.4f);
        Assert.True(detector.Pulse(beat + 1) < 0.01f);
    }

    [Fact]
    public void Reset_ForgetsTheLastBeat()
    {
        var detector = new OnsetDetector();
        Run(detector, 0, 1, Kicks(0.5, 0.5f));
        Assert.True(detector.Pulse(detector.LastBeatAt) > 0);

        detector.Reset();

        Assert.Equal(0f, detector.Pulse(2));
        Assert.Equal(double.NegativeInfinity, detector.LastBeatAt);
    }
}
