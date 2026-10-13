using System.Text.Json;
using Rambutan.Core.Models;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

public class SearchEntriesTests
{
    private static IReadOnlyList<CatalogEntry> Parse(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return SearchResponseParser.ParseEntries(doc.RootElement);
    }

    [Fact]
    public void AlbumsFilter_YieldsAlbumEntries()
    {
        var entries = Parse("search-albums.daft-punk.json");

        Assert.Equal(20, entries.Count);
        Assert.All(entries, e =>
        {
            var album = Assert.IsType<AlbumEntry>(e);
            Assert.StartsWith("MPREb", album.BrowseId);
            Assert.False(string.IsNullOrEmpty(album.Title));
        });
        Assert.Contains(entries, e => e.Title == "Random Access Memories");
    }

    [Fact]
    public void ArtistsFilter_YieldsArtistEntries()
    {
        var entries = Parse("search-artists.daft-punk.json");

        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            var artist = Assert.IsType<ArtistEntry>(e);
            Assert.StartsWith("UC", artist.BrowseId);
        });
        Assert.Contains(entries, e => e.Title == "Daft Punk");
    }

    [Fact]
    public void PlaylistsFilter_YieldsPlaylistEntries_WithoutVlPrefix()
    {
        var entries = Parse("search-playlists.daft-punk.json");

        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            var playlist = Assert.IsType<PlaylistEntry>(e);
            Assert.False(playlist.PlaylistId.StartsWith("VL", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void VideosFilter_YieldsPlayableSongEntries()
    {
        var entries = Parse("search-videos.daft-punk.json");

        Assert.Equal(20, entries.Count);
        Assert.All(entries, e =>
        {
            var song = Assert.IsType<SongEntry>(e);
            Assert.False(string.IsNullOrEmpty(song.Track.VideoId));
        });
    }
}
