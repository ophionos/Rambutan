using Rambutan.Core.Models;

namespace Rambutan.Integrations.Tests;

public sealed class SmtcArtworkTests
{
    private static TrackInfo Track(string id, string? thumbnail = null) =>
        new() { VideoId = id, Title = "t", Artists = [], ThumbnailUrl = thumbnail };

    [Fact]
    public void A_local_track_shows_its_kept_cover()
    {
        var artwork = SmtcArtwork.For(Track("local:7"), id => id == "local:7" ? "/covers/7.jpg" : null);

        Assert.Equal(new SmtcArtwork(null, "/covers/7.jpg"), artwork);
    }

    [Fact]
    public void A_local_track_without_a_cover_shows_none_and_never_its_thumbnail()
    {
        Assert.Same(SmtcArtwork.None, SmtcArtwork.For(Track("local:7", "https://example.com/a.jpg"), _ => null));
        Assert.Same(SmtcArtwork.None, SmtcArtwork.For(Track("local:7"), _ => ""));
        Assert.Same(SmtcArtwork.None, SmtcArtwork.For(Track("local:7"), localCover: null));
    }

    [Fact]
    public void A_YouTube_track_shows_its_thumbnail_and_never_asks_for_a_file()
    {
        var asked = false;

        var artwork = SmtcArtwork.For(Track("dQw4w9WgXcQ", "https://i.ytimg.com/vi/x/hq.jpg"),
            _ => { asked = true; return "/covers/x.jpg"; });

        Assert.Equal(new Uri("https://i.ytimg.com/vi/x/hq.jpg"), artwork.Url);
        Assert.Null(artwork.File);
        Assert.False(asked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a url")]
    public void A_YouTube_track_without_a_usable_thumbnail_shows_none(string? thumbnail) =>
        Assert.Same(SmtcArtwork.None, SmtcArtwork.For(Track("dQw4w9WgXcQ", thumbnail), null));
}
