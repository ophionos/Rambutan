using System.Text.Json;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

public class SearchResponseParserTests
{
    private static JsonDocument LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void ParseSongs_RecordedResponse_YieldsFullShelf()
    {
        using var doc = LoadFixture("search-songs.daft-punk.json");

        var tracks = SearchResponseParser.ParseSongs(doc.RootElement);

        Assert.Equal(20, tracks.Count);
        Assert.All(tracks, t =>
        {
            Assert.False(string.IsNullOrEmpty(t.VideoId));
            Assert.False(string.IsNullOrEmpty(t.Title));
        });
    }

    [Fact]
    public void ParseSongs_RecordedResponse_FirstResultFullyParsed()
    {
        using var doc = LoadFixture("search-songs.daft-punk.json");

        var track = SearchResponseParser.ParseSongs(doc.RootElement)[0];

        Assert.Equal("khnokW3Mw24", track.VideoId);
        Assert.Equal("Instant Crush (feat. Julian Casablancas)", track.Title);
        Assert.Equal(["Daft Punk", "Julian Casablancas"], track.Artists.Select(a => a.Name));
        // Artist links back the context menu's "Go to artist" (channel ids are UC…).
        Assert.All(track.Artists, a => Assert.StartsWith("UC", a.BrowseId));
        Assert.Equal("Random Access Memories", track.Album);
        Assert.Equal("MPREb_K8qWMWVqXGi", track.AlbumBrowseId);
        Assert.Equal(TimeSpan.FromSeconds(5 * 60 + 38), track.Duration);
        Assert.False(string.IsNullOrEmpty(track.ThumbnailUrl));
    }

    [Fact]
    public void ParseSongs_EmptyDocument_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"responseContext":{}}""");

        var tracks = SearchResponseParser.ParseSongs(doc.RootElement);

        Assert.Empty(tracks);
    }
}
