using Rambutan.Core.Settings;

namespace Rambutan.Core.Tests;

public class AudioQualityTests
{
    [Theory]
    [InlineData("high", AudioQuality.High)]
    [InlineData("data-saver", AudioQuality.DataSaver)]
    [InlineData("ultra", AudioQuality.High)]
    [InlineData(null, AudioQuality.High)]
    public void AudioQuality_ReadsTheSettingsFileSpelling(string? stored, AudioQuality expected) =>
        Assert.Equal(expected, AudioQualityNames.Parse(stored));

    [Theory]
    [InlineData(AudioQuality.High)]
    [InlineData(AudioQuality.DataSaver)]
    public void AudioQuality_RoundTripsThroughTheSettingsFile(AudioQuality quality) =>
        Assert.Equal(quality, AudioQualityNames.Parse(AudioQualityNames.Format(quality)));
}
