using static Rambutan.Audio.Tests.MixerTestKit;

namespace Rambutan.Audio.Tests;

/// <summary>
/// A seek replaces the current track's decode chain (<see cref="CrossfadeMixer.Replace"/>).
/// During a crossfade the incoming track has already been partly read, and a
/// ring can't be rewound.
/// </summary>
public class SeekDuringCrossfadeTests
{
    private const float Seeked = 0.25f;

    private static CrossfadeMixer FadingInto(BufferedTrack outgoing, BufferedTrack incoming, double fadeSeconds)
    {
        var mixer = new CrossfadeMixer();
        mixer.Play(outgoing);
        mixer.SetNext(incoming);
        mixer.BeginCrossfade(TimeSpan.FromSeconds(fadeSeconds));
        return mixer;
    }

    [Fact]
    public void SeekMidFade_DropsThePartReadNext_SoItNeverStartsMidway()
    {
        var outgoing = Dc("a", 0.5f, 3);
        var mixer = FadingInto(outgoing, Numbered("b", 3), 1);
        Run(mixer, 0.3);

        Assert.True(mixer.Replace(outgoing, Dc("a@seek", Seeked, 0.2)));
        var left = Left(Run(mixer, 0.5));

        // The seeked chain plays at full gain (a seek is a cut), then drains. Had the
        // part-read track stayed armed, a gapless handover would start it 0.3 s in.
        Assert.All(left[..(int)(0.2 * Rate)], v => Assert.Equal(Seeked, v));
        var tail = left[(int)(0.2 * Rate)..];
        var firstHeard = Array.Find(tail, v => v != 0f);
        Assert.True(firstHeard is 0f or FrameStep, $"the next track started at frame {firstHeard / FrameStep}");
        Assert.False(mixer.HasNext);
    }

    [Fact]
    public void SeekBeforeTheFadeReadAnything_KeepsTheNextArmed()
    {
        var outgoing = Dc("a", 0.5f, 3);
        var mixer = FadingInto(outgoing, Numbered("b", 3), 1);

        Assert.True(mixer.Replace(outgoing, Dc("a@seek", Seeked, 0.2)));
        var left = Left(Run(mixer, 0.3));

        Assert.True(mixer.HasNext || mixer.Current?.DisplayName == "b");
        Assert.Equal(FrameStep, left[(int)(0.2 * Rate)]); // gapless, from its first frame
    }

    [Fact]
    public void SeekLandingAfterThePromotion_IsRefused()
    {
        // The seek's new chain prebuffers for up to 2 s; the fade can finish meanwhile.
        var outgoing = Dc("a", 0.5f, 3);
        var incoming = Dc("b", 1f, 3);
        var mixer = FadingInto(outgoing, incoming, 0.1);
        Run(mixer, 0.2);
        Assert.Same(incoming, mixer.Current);

        Assert.False(mixer.Replace(outgoing, Dc("a@seek", Seeked, 0.2)));
        Assert.Same(incoming, mixer.Current);
    }

    [Fact]
    public void ClearNextMidFade_BringsTheOutgoingTrackBackWithoutAStep()
    {
        // What the coordinator does when a seek (or a queue edit) lands mid-fade.
        var mixer = FadingInto(Dc("a", 1f, 3), Dc("b", 0f, 3), 1);
        var fading = Left(Run(mixer, 0.5));

        mixer.ClearNext();
        var left = Left(Run(mixer, 0.6)); // it comes back over the 0.5 s it had faded

        Assert.True(left[0] - fading[^1] < 1e-3f, $"stepped from {fading[^1]} to {left[0]}");
        for (var i = 1; i < left.Length; i++)
        {
            Assert.True(left[i] >= left[i - 1], $"gain fell at frame {i}");
            Assert.True(left[i] - left[i - 1] < 1e-3f, $"gain stepped at frame {i}: {left[i - 1]} → {left[i]}");
        }
        Assert.Equal(1f, left[^1]);
    }
}
