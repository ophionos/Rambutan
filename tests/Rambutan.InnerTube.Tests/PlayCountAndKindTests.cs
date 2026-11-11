using System.Text.Json;
using Rambutan.Core.Models;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// TrackInfo.PlayCount and TrackInfo.Kind: the "1.2B plays" run and the row's
/// musicVideoType, which the surfaces that list songs and videos together need.
/// </summary>
public class PlayCountAndKindTests
{
    private static JsonDocument LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void ArtistTopSongs_CarryPlaysAndAreSongs()
    {
        using var doc = LoadFixture("browse-artist.daft-punk.json");

        var artist = BrowseParsers.ParseArtist("UCGJW5vP2dSWOyAVVmFl7w7g", doc.RootElement);

        Assert.NotEmpty(artist.TopSongs);
        Assert.Equal(1_800_000_000L, artist.TopSongs[0].PlayCount);
        Assert.All(artist.TopSongs, t =>
        {
            Assert.NotNull(t.PlayCount);
            Assert.Equal(TrackKind.Song, t.Kind);
        });
    }

    [Fact]
    public void SongSearch_RowsAreSongs_WithPlays()
    {
        using var doc = LoadFixture("search-songs.daft-punk.json");

        var songs = SearchResponseParser.ParseSongs(doc.RootElement);

        Assert.NotEmpty(songs);
        Assert.Equal(1_200_000_000L, songs[0].PlayCount);
        Assert.All(songs, t => Assert.Equal(TrackKind.Song, t.Kind));
    }

    [Fact]
    public void VideoSearch_RowsAreVideos_WithViews_AndTheCountIsNoArtist()
    {
        using var doc = LoadFixture("search-videos.daft-punk.json");

        var videos = SearchResponseParser.ParseSongs(doc.RootElement);

        Assert.NotEmpty(videos);
        Assert.Equal(609_000_000L, videos[0].PlayCount);
        Assert.All(videos, t =>
        {
            Assert.Equal(TrackKind.Video, t.Kind);
            Assert.DoesNotContain(t.Artists, a => a.Name.EndsWith(" views", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Overview_TellsSongsFromVideos()
    {
        using var doc = LoadFixture("search-all.daft-punk.json");

        var tracks = SearchResponseParser.ParseOverview(doc.RootElement).Results
            .OfType<SongEntry>().Select(e => e.Track).ToArray();

        Assert.NotEmpty(tracks);
        Assert.All(tracks, t => Assert.NotNull(t.Kind));
        var song = tracks.Single(t => t.Title == "Something About Us");
        Assert.Equal(TrackKind.Song, song.Kind);
        Assert.Equal(226_000_000L, song.PlayCount);
        // The song of the same name sits in the top result's card, not here.
        var video = tracks.Single(t => t.Title == "One More Time");
        Assert.Equal(TrackKind.Video, video.Kind);
        Assert.Equal(619_000_000L, video.PlayCount);
        Assert.Contains(tracks, t => t.Kind == TrackKind.Song);
        Assert.Contains(tracks, t => t.Kind == TrackKind.Video);
    }

    [Fact]
    public void AlbumRows_ListVideos_AndPlayAsSongs()
    {
        using var doc = LoadFixture("browse-album.ram.json");

        var row = BrowseParsers.ParseAlbum("MPREb_K8qWMWVqXGi", doc.RootElement).Tracks[4];

        // Signed out, an album page lists the music videos; the row that plays
        // is the audio track, and the fallback is the video again.
        Assert.Equal(TrackKind.Video, row.Kind);
        Assert.Equal(1_200_000_000L, row.PlayCount);
        var played = AlbumAudio.Prefer(row);
        Assert.Equal(TrackKind.Song, played.Kind);
        Assert.Equal(TrackKind.Video, AlbumAudio.Fallback(played)!.Kind);
    }
}
