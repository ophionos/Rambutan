using System.Text.Json;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

public class NextResponseParserTests
{
    private static JsonDocument LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void ParseWatchQueue_RecordedRadio_YieldsFullQueue()
    {
        using var doc = LoadFixture("next-radio.khnokw3mw24.json");

        var tracks = NextResponseParser.ParseWatchQueue(doc.RootElement);

        Assert.Equal(50, tracks.Count);
        // The seed track leads its own radio.
        Assert.Equal("khnokW3Mw24", tracks[0].VideoId);
        Assert.Equal("Instant Crush (feat. Julian Casablancas)", tracks[0].Title);
        Assert.Equal(
            ["Daft Punk", "Julian Casablancas"], tracks[0].Artists.Select(a => a.Name));
        Assert.Equal("Random Access Memories", tracks[0].Album);
        // The album link backs the player bar's title link.
        Assert.Equal("MPREb_K8qWMWVqXGi", tracks[0].AlbumBrowseId);
        Assert.Equal(TimeSpan.FromSeconds(5 * 60 + 38), tracks[0].Duration);
        Assert.All(tracks, t =>
        {
            Assert.False(string.IsNullOrEmpty(t.VideoId));
            Assert.False(string.IsNullOrEmpty(t.Title));
            Assert.False(string.IsNullOrEmpty(t.ThumbnailUrl));
        });
        // Radio rows must stay linkable for "Go to artist".
        Assert.Contains(tracks, t => t.Artists.Any(a => a.BrowseId is not null));
    }

    [Fact]
    public void ParseWatchQueue_RadioFromAMusicVideo_HasNoAlbums_BecauseYtmSendsNone()
    {
        // Seeded from Adele's "Hello" video: every row is a music video whose byline
        // is "artist • views • likes", with no album run to read. The
        // parser leaves Album empty rather than guessing; scrobbles go without it.
        using var doc = LoadFixture("next-radio.yqhsxmglc9a.json");

        var tracks = NextResponseParser.ParseWatchQueue(doc.RootElement);

        Assert.Equal(50, tracks.Count);
        Assert.Equal("YQHsXMglC9A", tracks[0].VideoId);
        Assert.Equal(["Adele"], tracks[0].Artists.Select(a => a.Name));
        Assert.All(tracks, t =>
        {
            Assert.Null(t.Album);
            Assert.Null(t.AlbumBrowseId);
            Assert.NotEmpty(t.Artists);
            Assert.DoesNotContain(t.Artists, a => a.Name.Contains("views", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ParseWatchQueue_EmptyDocument_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"responseContext":{}}""");

        Assert.Empty(NextResponseParser.ParseWatchQueue(doc.RootElement));
    }
}
