using System.Text.Json;
using Rambutan.Core.Models;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// TrackInfo.SongId: the videoId behind a row's "View song credits" item, which is
/// how YouTube Music pairs an album page's music videos with the audio tracks that
/// search and radio hand out. browse-playlist.ram-audio.json is the album's
/// own audio playlist (VLOLAK5uy_kNhM2…), recorded anonymously to pin that pairing.
/// </summary>
public class SongIdParsingTests
{
    private const string AlbumId = "MPREb_K8qWMWVqXGi";

    private static JsonDocument LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void AlbumRows_NameTheirAudioTrackCounterpart()
    {
        using var doc = LoadFixture("browse-album.ram.json");

        var album = BrowseParsers.ParseAlbum(AlbumId, doc.RootElement);

        var instantCrush = album.Tracks[4];
        Assert.Equal("a5uQMwRMHcs", instantCrush.VideoId);   // the music video
        Assert.Equal("khnokW3Mw24", instantCrush.SongId);    // the audio track search gives
        Assert.All(album.Tracks, t =>
        {
            Assert.NotNull(t.SongId);
            Assert.NotEqual(t.VideoId, t.SongId);
        });
    }

    [Fact]
    public void AlbumRows_PairWithTheAlbumsAudioPlaylist_PositionAndSetVideoId()
    {
        using var albumDoc = LoadFixture("browse-album.ram.json");
        using var audioDoc = LoadFixture("browse-playlist.ram-audio.json");

        var album = BrowseParsers.ParseAlbum(AlbumId, albumDoc.RootElement);
        var audio = BrowseParsers.ParsePlaylist(album.AudioPlaylistId!, audioDoc.RootElement);

        Assert.Equal(album.Tracks.Count, audio.Tracks.Count);
        for (var i = 0; i < album.Tracks.Count; i++)
        {
            // Two independent signals agree: the credits target is the audio
            // playlist's row at the same slot, and the slots share a setVideoId.
            Assert.Equal(audio.Tracks[i].VideoId, album.Tracks[i].SongId);
            Assert.Equal(audio.Tracks[i].SetVideoId, album.Tracks[i].SetVideoId);
            Assert.True(album.Tracks[i].Identity.SameSong(audio.Tracks[i].Identity));
        }
    }

    [Fact]
    public void AnAlbumPage_PlaysTheAlbumsAudio_NotItsMusicVideos()
    {
        // Signed out, all 13 rows list the music video. Played as an album
        // page plays them, each is the audio playlist's track at its slot, and
        // keeps the listed video to fall back to.
        using var albumDoc = LoadFixture("browse-album.ram.json");
        using var audioDoc = LoadFixture("browse-playlist.ram-audio.json");

        var album = BrowseParsers.ParseAlbum(AlbumId, albumDoc.RootElement);
        var audio = BrowseParsers.ParsePlaylist(album.AudioPlaylistId!, audioDoc.RootElement);
        var played = album.Tracks.Select(t => AlbumAudio.Prefer(t)).ToList();

        Assert.Equal(13, played.Count);
        Assert.Equal(audio.Tracks.Select(t => t.VideoId), played.Select(t => t.VideoId));
        Assert.Equal(album.Tracks.Select(t => t.VideoId), played.Select(t => t.ListedVideoId));
        Assert.Equal(album.Tracks.Select(t => t.Title), played.Select(t => t.Title));
    }

    [Fact]
    public void AudioTrackRows_NameThemselves()
    {
        using var audioDoc = LoadFixture("browse-playlist.ram-audio.json");
        using var searchDoc = LoadFixture("search-songs.daft-punk.json");

        var audio = BrowseParsers.ParsePlaylist("OLAK5uy_kNhM2yaBTOVwrcZJepB1C9P3-n5_Sfy5c", audioDoc.RootElement);
        var search = SearchResponseParser.ParseSongs(searchDoc.RootElement);

        Assert.All(audio.Tracks, t => Assert.Equal(t.VideoId, t.SongId));
        Assert.All(search.Where(t => t.SongId is not null), t => Assert.Equal(t.VideoId, t.SongId));
        Assert.Equal("khnokW3Mw24", search[0].SongId);
    }

    [Fact]
    public void SearchResult_MatchesTheAlbumRow_TheIssueRepro()
    {
        using var albumDoc = LoadFixture("browse-album.ram.json");
        using var searchDoc = LoadFixture("search-songs.daft-punk.json");

        var album = BrowseParsers.ParseAlbum(AlbumId, albumDoc.RootElement);
        var fromSearch = SearchResponseParser.ParseSongs(searchDoc.RootElement)[0];

        Assert.Equal("Instant Crush (feat. Julian Casablancas)", fromSearch.Title);
        var marked = album.Tracks.Where(t => t.Identity.SameSong(fromSearch.Identity)).ToList();
        Assert.Equal("a5uQMwRMHcs", Assert.Single(marked).VideoId);
    }

    [Fact]
    public void MusicVideoRows_OutsideAlbumPages_HaveNoSongId()
    {
        using var videosDoc = LoadFixture("search-videos.daft-punk.json");
        using var playlistDoc = LoadFixture("browse-playlist.80s-pop.json");
        using var omvRadioDoc = LoadFixture("next-radio.yqhsxmglc9a.json");

        var videos = SearchResponseParser.ParseEntries(videosDoc.RootElement).OfType<SongEntry>().ToList();
        Assert.NotEmpty(videos);
        Assert.All(videos, v => Assert.Null(v.Track.SongId));

        // The playlist mixes both kinds: only its audio-track rows carry credits.
        var playlist = BrowseParsers.ParsePlaylist("x", playlistDoc.RootElement);
        Assert.Contains(playlist.Tracks, t => t.SongId is null);
        Assert.All(playlist.Tracks.Where(t => t.SongId is not null), t => Assert.Equal(t.VideoId, t.SongId));

        var omvRadio = NextResponseParser.ParseWatchQueue(omvRadioDoc.RootElement);
        Assert.All(omvRadio, t => Assert.Null(t.SongId));
    }

    [Fact]
    public void RadioRows_CarryTheirCredits()
    {
        using var doc = LoadFixture("next-radio.khnokw3mw24.json");

        var queue = NextResponseParser.ParseWatchQueue(doc.RootElement);

        Assert.Equal("khnokW3Mw24", queue[0].SongId);
        Assert.All(queue.Where(t => t.SongId is not null), t => Assert.Equal(t.VideoId, t.SongId));
    }
}
