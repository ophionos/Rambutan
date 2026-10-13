using static Rambutan.Audio.Tests.MixerTestKit;

namespace Rambutan.Audio.Tests;

public class CrossfadeTests
{
    /// <summary>Largest frame-to-frame change a fade may make: a 1 s equal-power
    /// curve moves at most (π/2)/48000 ≈ 3.3e-5 per frame; a step is far above.</summary>
    private const float MaxFadeStep = 1e-3f;

    private static (CrossfadeMixer Mixer, List<BufferedTrack> Started) MixerWith(
        BufferedTrack current, BufferedTrack next)
    {
        var mixer = new CrossfadeMixer();
        var started = new List<BufferedTrack>();
        mixer.Play(current);
        mixer.TrackStarted += started.Add;
        mixer.SetNext(next);
        return (mixer, started);
    }

    private static void AssertRisesWithoutSteps(float[] left)
    {
        for (var i = 1; i < left.Length; i++)
        {
            Assert.True(left[i] >= left[i - 1], $"gain fell at frame {i}: {left[i - 1]} → {left[i]}");
            Assert.True(left[i] - left[i - 1] < MaxFadeStep, $"gain stepped at frame {i}: {left[i - 1]} → {left[i]}");
        }
    }

    [Fact]
    public void Fade_RunsTheEqualPowerCurve_AndPromotesOnceAtTheEnd()
    {
        // A silent outgoing track leaves only the incoming one's gain in the output.
        var incoming = Dc("b", 1f, 3);
        var (mixer, started) = MixerWith(Dc("a", 0f, 3), incoming);

        mixer.BeginCrossfade(TimeSpan.FromSeconds(1));
        var left = Left(Run(mixer, 1.5));

        AssertRisesWithoutSteps(left);
        Assert.Equal(1f, left[^1]);
        Assert.Equal((float)Math.Sin(0.5 * Math.PI / 2), left[Rate / 2], 1e-4f);
        Assert.Equal([incoming], started);
        Assert.Same(incoming, mixer.Current);
    }

    [Fact]
    public void OutgoingEndingEarly_PromotesAtOnce_ButTheIncomingKeepsFadingIn()
    {
        // The coordinator sizes a fade by the duration it was told; a track that
        // ends before that (a duration rounded up, a short last packet) used to
        // promote the incoming track mid-fade and jump it straight to full gain.
        var incoming = Dc("b", 1f, 3);
        var (mixer, started) = MixerWith(Dc("a", 0f, 0.25), incoming);

        mixer.BeginCrossfade(TimeSpan.FromSeconds(1));
        var left = Left(Run(mixer, 1.5));

        AssertRisesWithoutSteps(left);
        Assert.Equal(1f, left[^1]);
        Assert.Equal(1f, left[Rate]); // full gain when the fade was due to end, not later
        Assert.True(left[Rate / 2] < 0.95f, "the incoming track jumped to full gain");
        Assert.Equal([incoming], started);
        Assert.Same(incoming, mixer.Current);
    }

    [Fact]
    public void OutgoingEndingEarly_TheIncomingTrackLosesNoAudio()
    {
        var incoming = Numbered("b", 3);
        var (mixer, _) = MixerWith(Dc("a", 0f, 0.25), incoming);

        mixer.BeginCrossfade(TimeSpan.FromSeconds(1));
        Run(mixer, 1.5);
        var after = Left(Run(mixer, 0.01));

        // At full gain, frame n of the output is frame n of the incoming track.
        var frame = (int)Math.Round(after[0] / FrameStep) - 1;
        Assert.Equal((int)(1.5 * Rate), frame);
    }
}
