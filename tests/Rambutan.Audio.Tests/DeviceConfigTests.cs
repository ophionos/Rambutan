namespace Rambutan.Audio.Tests;

/// <summary>The playback device's config: SoundFlow's default, plus the stream's name.</summary>
public class DeviceConfigTests
{
    [Fact]
    public void TheStream_IsNamedRambutan()
    {
        var config = PlaybackEngine.DeviceConfig();

        Assert.Equal("Rambutan", config.Pulse?.StreamNamePlayback);
    }

    [Fact]
    public void TheRest_IsSoundFlowsOwnDefault()
    {
        // MiniAudioEngine.GetDefaultDeviceConfig (1.4.2): a 960-frame period, shared mode.
        var config = PlaybackEngine.DeviceConfig();

        Assert.Equal(960u, config.PeriodSizeInFrames);
        Assert.Equal("Shared", config.Playback.ShareMode.ToString());
        Assert.Null(config.Wasapi);
        Assert.Null(config.Alsa);
    }
}
