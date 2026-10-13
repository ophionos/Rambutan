using System.Text.Json;
using Rambutan.Core.Models;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

public class BrowseParsersTests
{
    private static JsonDocument LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void ParseHome_YieldsShelvesWithTypedEntries()
    {
        using var doc = LoadFixture("browse-home.json");

        var shelves = BrowseParsers.ParseHome(doc.RootElement).Shelves;

        Assert.NotEmpty(shelves);
        Assert.All(shelves, shelf =>
        {
            Assert.False(string.IsNullOrEmpty(shelf.Title));
            Assert.NotEmpty(shelf.Entries);
        });

        var entries = shelves.SelectMany(s => s.Entries).ToList();
        Assert.Contains(entries, e => e is PlaylistEntry);
        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrEmpty(e.Title));
            Assert.False(string.IsNullOrEmpty(e.ThumbnailUrl));
        });

        // Playlist ids must be de-VL'd, ready for GetPlaylistAsync.
        Assert.All(entries.OfType<PlaylistEntry>(), p =>
            Assert.False(p.PlaylistId.StartsWith("VL", StringComparison.Ordinal)));
    }

    [Fact]
    public void ParseAlbum_HeaderAndTracks()
    {
        using var doc = LoadFixture("browse-album.ram.json");

        var album = BrowseParsers.ParseAlbum("MPREb_K8qWMWVqXGi", doc.RootElement);

        Assert.Equal("Random Access Memories", album.Title);
        Assert.Equal("Daft Punk", album.Artist);
        Assert.Equal(13, album.Tracks.Count);
        Assert.False(string.IsNullOrEmpty(album.ThumbnailUrl));
        Assert.Equal("Give Life Back to Music", album.Tracks[0].Title);
        Assert.All(album.Tracks, t =>
        {
            Assert.False(string.IsNullOrEmpty(t.VideoId));
            // Rows omit what the header states; the parser must fill both in.
            Assert.Equal("Random Access Memories", t.Album);
            // Rows don't link their own album; the page's browse id stands in.
            Assert.Equal("MPREb_K8qWMWVqXGi", t.AlbumBrowseId);
            // Rows have no art; the album's stands in, for the queue and player bar.
            Assert.Equal(album.ThumbnailUrl, t.ThumbnailUrl);
            Assert.NotEmpty(t.Artists);
            Assert.NotNull(t.Duration);
        });
    }

    [Fact]
    public void ParseAlbum_NumbersRowsAndLinksTheHeaderArtist()
    {
        using var doc = LoadFixture("browse-album.ram.json");

        var album = BrowseParsers.ParseAlbum("MPREb_K8qWMWVqXGi", doc.RootElement);

        // The strapline's link, so the header artist can navigate.
        Assert.Equal("UCRr1xG_2WIDs18a6cIiCxeA", album.ArtistBrowseId);
        // Rows carry the album's own numbering, 1..13 in order.
        Assert.Equal(Enumerable.Range(1, 13), album.Tracks.Select(t => t.TrackNumber ?? 0));
    }

    [Fact]
    public void ParsePlaylist_LeavesTrackNumbersUnset()
    {
        using var doc = LoadFixture("browse-playlist.80s-pop.json");

        var playlist = BrowseParsers.ParsePlaylist(
            "RDCLAK5uy_k1Wu8QbZASiGVqr1wmie9NIYo38aBqscQ", doc.RootElement);

        // A playlist's order isn't a track number; only album rows get one.
        Assert.All(playlist.Tracks, t => Assert.Null(t.TrackNumber));
    }

    [Fact]
    public void ParsePlaylist_HeaderTracksAndContinuation()
    {
        using var doc = LoadFixture("browse-playlist.80s-pop.json");

        var playlist = BrowseParsers.ParsePlaylist(
            "RDCLAK5uy_k1Wu8QbZASiGVqr1wmie9NIYo38aBqscQ", doc.RootElement);

        Assert.Equal("'80s Pop", playlist.Title);
        Assert.Equal(100, playlist.Tracks.Count); // 100 rows + 1 inline continuation item
        Assert.NotNull(playlist.Continuation);
        Assert.Contains("songs", playlist.SecondSubtitle);
        Assert.All(playlist.Tracks, t =>
        {
            Assert.False(string.IsNullOrEmpty(t.VideoId));
            Assert.False(string.IsNullOrEmpty(t.Title));
            Assert.NotNull(t.Duration);
            // Row identity for remove-from-playlist.
            Assert.False(string.IsNullOrEmpty(t.SetVideoId));
        });
        Assert.Equal("56B44F6D10557CC6", playlist.Tracks[0].SetVideoId);
        // Rows that name an album link it; a name without a link stays unlinked.
        Assert.Contains(playlist.Tracks, t => t.AlbumBrowseId is not null);
        Assert.All(playlist.Tracks, t =>
        {
            if (t.AlbumBrowseId is not null)
            {
                Assert.StartsWith("MPREb_", t.AlbumBrowseId);
                Assert.False(string.IsNullOrEmpty(t.Album));
            }
        });
    }

    [Fact]
    public void ParsePlaylistContinuation_AppendAction_YieldsMoreTracks()
    {
        using var doc = LoadFixture("browse-playlist-continuation.tracks.json");

        var page = BrowseParsers.ParsePlaylistContinuation("x", doc.RootElement);

        Assert.Equal(100, page.Tracks.Count);
        Assert.All(page.Tracks, t => Assert.False(string.IsNullOrEmpty(t.VideoId)));
    }

    [Fact]
    public void ParsePlaylistContinuation_RelatedOnlyResponse_YieldsNoTracksAndEnds()
    {
        using var doc = LoadFixture("browse-playlist-continuation.related-only.json");

        var page = BrowseParsers.ParsePlaylistContinuation("x", doc.RootElement);

        Assert.Empty(page.Tracks);
        Assert.Null(page.Continuation);
    }

    // ---- Handcrafted shapes (per-item behavior not covered by fixtures) --------

    private static JsonDocument HomeWithCarouselItems(string itemsJson) => JsonDocument.Parse(
        """
        {"contents":{"singleColumnBrowseResultsRenderer":{"tabs":[{"tabRenderer":{"content":
        {"sectionListRenderer":{"contents":[{"musicCarouselShelfRenderer":{
        "header":{"musicCarouselShelfBasicHeaderRenderer":{"title":{"runs":[{"text":"Shelf"}]}}},
        "contents":[@ITEMS@]}}]}}}}]}}}
        """.Replace("@ITEMS@", itemsJson));

    private const string ArtistEndpoint =
        """
        "navigationEndpoint":{"browseEndpoint":{"browseId":"UC1",
        "browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":
        {"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}
        """;

    [Fact]
    public void ParseHome_SongCard_PicksArtistRunsByPageType()
    {
        using var doc = HomeWithCarouselItems(
            """
            {"musicTwoRowItemRenderer":{
              "title":{"runs":[{"text":"Song Title"}]},
              "subtitle":{"runs":[
                {"text":"Song"},{"text":" • "},
                {"text":"Some Artist",@ARTIST@},
                {"text":" • "},{"text":"1.2M plays"}]},
              "navigationEndpoint":{"watchEndpoint":{"videoId":"vid00000001"}}}}
            """.Replace("@ARTIST@", ArtistEndpoint));

        var song = Assert.IsType<SongEntry>(
            Assert.Single(Assert.Single(BrowseParsers.ParseHome(doc.RootElement).Shelves).Entries));

        // The artist slot must get the artist run, never the whole subtitle line.
        Assert.Equal([new ArtistRef("Some Artist", "UC1")], song.Track.Artists);
        Assert.Equal("Song • Some Artist • 1.2M plays", song.Subtitle);
    }

    [Fact]
    public void ParseHome_SongCardWithoutArtistRuns_FallsBackToSubtitle()
    {
        using var doc = HomeWithCarouselItems(
            """
            {"musicTwoRowItemRenderer":{
              "title":{"runs":[{"text":"Song Title"}]},
              "subtitle":{"runs":[{"text":"Some Artist"}]},
              "navigationEndpoint":{"watchEndpoint":{"videoId":"vid00000001"}}}}
            """);

        var song = Assert.IsType<SongEntry>(
            Assert.Single(Assert.Single(BrowseParsers.ParseHome(doc.RootElement).Shelves).Entries));

        Assert.Equal([new ArtistRef("Some Artist")], song.Track.Artists);
    }

    [Theory]
    // A "Listen again" card whose artist isn't a link.
    [InlineData("""[{"text":"Song"},{"text":" • "},{"text":"DJ Electrohead"},{"text":" • "},{"text":"3:12"}]""", "DJ Electrohead", 192)]
    [InlineData("""[{"text":"Video"},{"text":" • "},{"text":"Some Artist"},{"text":" • "},{"text":"1.2M views"}]""", "Some Artist", null)]
    [InlineData("""[{"text":"Some Artist"},{"text":" • "},{"text":"2013"}]""", "Some Artist", null)]
    // A name that starts with a number is still a name.
    [InlineData("""[{"text":"21 Savage"}]""", "21 Savage", null)]
    [InlineData("""[{"text":"Song"},{"text":" • "},{"text":"50 Cent"},{"text":" • "},{"text":"2.1M plays"}]""", "50 Cent", null)]
    public void ParseHome_PlainSubtitleRuns_LeaveTypeCountsAndYearsOutOfTheArtist(
        string runs, string artist, int? seconds)
    {
        using var doc = HomeWithCarouselItems(
            """
            {"musicTwoRowItemRenderer":{
              "title":{"runs":[{"text":"Hit the Floor"}]},
              "subtitle":{"runs":@RUNS@},
              "navigationEndpoint":{"watchEndpoint":{"videoId":"vid00000001"}}}}
            """.Replace("@RUNS@", runs));

        var song = Assert.IsType<SongEntry>(
            Assert.Single(Assert.Single(BrowseParsers.ParseHome(doc.RootElement).Shelves).Entries));

        Assert.Equal([new ArtistRef(artist)], song.Track.Artists);
        Assert.Equal(seconds is { } s ? TimeSpan.FromSeconds(s) : null, song.Track.Duration);
    }

    [Fact]
    public void ParseHome_MalformedItem_IsSkippedNotFatal()
    {
        // videoId is a number: GetString() would throw — the item must be dropped
        // while its healthy neighbor survives.
        using var doc = HomeWithCarouselItems(
            """
            {"musicTwoRowItemRenderer":{
              "title":{"runs":[{"text":"Broken"}]},
              "navigationEndpoint":{"watchEndpoint":{"videoId":123}}}},
            {"musicTwoRowItemRenderer":{
              "title":{"runs":[{"text":"Healthy"}]},
              "subtitle":{"runs":[{"text":"Artist"}]},
              "navigationEndpoint":{"watchEndpoint":{"videoId":"vid00000002"}}}}
            """);

        var entry = Assert.Single(Assert.Single(BrowseParsers.ParseHome(doc.RootElement).Shelves).Entries);
        Assert.Equal("Healthy", entry.Title);
    }

    [Fact]
    public void ParseArtist_HeaderTopSongsAndShelves()
    {
        using var doc = LoadFixture("browse-artist.daft-punk.json");

        var artist = BrowseParsers.ParseArtist("UCRr1xG_2WIDs18a6cIiCxeA", doc.RootElement);

        Assert.Equal("Daft Punk", artist.Name);
        Assert.False(string.IsNullOrEmpty(artist.ThumbnailUrl));
        Assert.NotEmpty(artist.TopSongs);
        Assert.All(artist.TopSongs, t => Assert.False(string.IsNullOrEmpty(t.VideoId)));

        Assert.NotEmpty(artist.Shelves);
        var albumEntries = artist.Shelves.SelectMany(s => s.Entries).OfType<AlbumEntry>().ToList();
        Assert.NotEmpty(albumEntries);
        Assert.All(albumEntries, a => Assert.StartsWith("MPREb", a.BrowseId));
    }
}
