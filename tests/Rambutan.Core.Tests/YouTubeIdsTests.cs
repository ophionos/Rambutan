using Rambutan.Core.Models;

namespace Rambutan.Core.Tests;

public class YouTubeIdsTests
{
    [Theory]
    [InlineData("khnokW3Mw24")]
    [InlineData("abc-DEF_123")]
    [InlineData("a")]
    public void YouTubeShapedIds_AreVideoIds(string id)
    {
        Assert.True(YouTubeIds.IsVideoId(id));
        YouTubeIds.Require(id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("local:1")]
    [InlineData("local:12345")]
    [InlineData("khnokW3Mw2.")]
    [InlineData("khnok W3Mw2")]
    [InlineData("khnokW3Mw2é")]
    [InlineData("../khnokW3Mw")]
    public void OtherIds_AreRefused(string? id)
    {
        Assert.False(YouTubeIds.IsVideoId(id));
        var refused = Assert.Throws<NotAYouTubeTrackException>(() => YouTubeIds.Require(id));
        Assert.Equal(id, refused.TrackId);
    }
}
