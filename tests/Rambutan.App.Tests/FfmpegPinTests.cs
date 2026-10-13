using FFmpeg.AutoGen.Abstractions;
using Rambutan.Streams;

namespace Rambutan.App.Tests;

/// <summary>
/// Streams pins the BtbN build and Audio pins the FFmpeg.AutoGen binding; neither can
/// see the other, so this is where the two pins meet. Updating one without the other
/// downloads libraries the binding can't load.
/// </summary>
public sealed class FfmpegPinTests
{
    [Fact]
    public void Pinned_library_majors_match_the_binding()
    {
        var binding = new Dictionary<string, int>
        {
            ["avcodec"] = ffmpeg.LIBAVCODEC_VERSION_MAJOR,
            ["avformat"] = ffmpeg.LIBAVFORMAT_VERSION_MAJOR,
            ["avutil"] = ffmpeg.LIBAVUTIL_VERSION_MAJOR,
            ["swresample"] = ffmpeg.LIBSWRESAMPLE_VERSION_MAJOR,
        };

        var pinned = FfmpegManager.RequiredLibraries.ToDictionary(l => l.Name, l => l.Major);

        Assert.Equal(binding, pinned);
    }
}
