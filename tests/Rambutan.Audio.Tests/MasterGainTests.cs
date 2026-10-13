using static Rambutan.Audio.Tests.MixerTestKit;

namespace Rambutan.Audio.Tests;

public class MasterGainTests
{
    [Fact]
    public void Gain_SettlesExactlyOnTheTarget()
    {
        var mixer = new CrossfadeMixer();
        mixer.Play(Dc("a", 1f, 4));

        mixer.MasterGain = 0.5;
        Run(mixer, 1);
        var settled = Run(mixer, 0.5);

        // An exponential approach in float stops a few ulps short of the target
        // and then multiplies every sample by the wrong gain forever.
        Assert.All(settled, sample => Assert.Equal(0.5f, sample));
    }

    [Fact]
    public void Mute_SettlesOnExactSilence_NotOnSubnormals()
    {
        var mixer = new CrossfadeMixer();
        mixer.Play(Dc("a", 1f, 4));

        mixer.MasterGain = 0;
        Run(mixer, 2);
        var settled = Run(mixer, 0.5);

        Assert.All(settled, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Gain_ReturnsExactlyToUnity()
    {
        var mixer = new CrossfadeMixer();
        mixer.Play(Dc("a", 1f, 4));
        mixer.MasterGain = 0.5;
        Run(mixer, 1);

        mixer.MasterGain = 1;
        Run(mixer, 1);
        var settled = Run(mixer, 0.5);

        Assert.All(settled, sample => Assert.Equal(1f, sample));
    }

    [Fact]
    public void Gain_RampsSmoothly_AndKeepsTheChannelsTogether()
    {
        var mixer = new CrossfadeMixer();
        mixer.Play(Dc("a", 1f, 4));

        mixer.MasterGain = 0;
        var ramp = Run(mixer, 0.2);

        for (var frame = 0; frame < ramp.Length / Channels; frame++)
            Assert.Equal(ramp[frame * Channels], ramp[frame * Channels + 1]);
        var left = Left(ramp);
        for (var i = 1; i < left.Length; i++)
        {
            Assert.True(left[i] <= left[i - 1], $"gain rose at frame {i}");
            Assert.True(left[i - 1] - left[i] < 0.01f, $"gain stepped at frame {i}");
        }
        Assert.True(left[0] > 0.99f, "the ramp must start from the old gain");
    }
}
