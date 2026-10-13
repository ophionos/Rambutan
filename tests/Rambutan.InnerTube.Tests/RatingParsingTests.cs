using System.Text.Json;
using Rambutan.Core.Models;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// Like status on every surface that reports it. The
/// recordings are anonymous, so they only ever say INDIFFERENT; the LIKE and
/// DISLIKE shapes come from the *.rated.synthetic.json fixtures, which are the
/// recorded rows with only likeStatus edited.
/// </summary>
public class RatingParsingTests
{
    private static JsonDocument LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void Album_RecordedRows_AreNotRated()
    {
        using var doc = LoadFixture("browse-album.ram.json");

        var album = BrowseParsers.ParseAlbum("MPREb_K8qWMWVqXGi", doc.RootElement);

        // "Not rated" is a real answer here, distinct from unknown (null).
        Assert.All(album.Tracks, t => Assert.Equal(Rating.None, t.Rating));
    }

    [Fact]
    public void Playlist_RecordedRowsAndContinuation_AreNotRated()
    {
        using var first = LoadFixture("browse-playlist.80s-pop.json");
        using var more = LoadFixture("browse-playlist-continuation.tracks.json");

        var playlist = BrowseParsers.ParsePlaylist("x", first.RootElement);
        var page = BrowseParsers.ParsePlaylistContinuation("x", more.RootElement);

        Assert.All(playlist.Tracks.Concat(page.Tracks), t => Assert.Equal(Rating.None, t.Rating));
    }

    [Fact]
    public void Artist_RecordedTopSongs_AreNotRated()
    {
        using var doc = LoadFixture("browse-artist.daft-punk.json");

        var artist = BrowseParsers.ParseArtist("UCRr1xG_2WIDs18a6cIiCxeA", doc.RootElement);

        Assert.All(artist.TopSongs, t => Assert.Equal(Rating.None, t.Rating));
    }

    [Fact]
    public void Playlist_RatedRows_MapEachStatus()
    {
        using var doc = LoadFixture("browse-playlist.rated.synthetic.json");

        var playlist = BrowseParsers.ParsePlaylist("x", doc.RootElement);

        Assert.Equal(
            [Rating.Like, Rating.Dislike, Rating.None],
            playlist.Tracks.Select(t => t.Rating));
    }

    [Fact]
    public void SearchSongs_CarryNoStatus_AreUnknownNotUnrated()
    {
        using var doc = LoadFixture("search-songs.daft-punk.json");

        var tracks = SearchResponseParser.ParseSongs(doc.RootElement);

        Assert.NotEmpty(tracks);
        Assert.All(tracks, t => Assert.Null(t.Rating));
    }

    [Fact]
    public void Radio_OnlyTheSeedIsRated()
    {
        using var doc = LoadFixture("next-radio.khnokw3mw24.json");

        var tracks = NextResponseParser.ParseWatchQueue(doc.RootElement);

        Assert.Equal(Rating.None, tracks[0].Rating);
        Assert.All(tracks.Skip(1), t => Assert.Null(t.Rating));
    }

    [Fact]
    public void ParseRating_ReadsThePlayerOverlayForTheRequestedVideo()
    {
        using var recorded = LoadFixture("next-radio.khnokw3mw24.json");
        using var liked = LoadFixture("next-rated.synthetic.json");

        Assert.Equal(Rating.None, NextResponseParser.ParseRating(recorded.RootElement, "khnokW3Mw24"));
        Assert.Equal(Rating.Like, NextResponseParser.ParseRating(liked.RootElement, "khnokW3Mw24"));
        // The overlay rates one video; asking about another is "unknown".
        Assert.Null(NextResponseParser.ParseRating(liked.RootElement, "someOtherId"));
    }

    [Theory]
    [InlineData("\"LIKE\"", Rating.Like)]
    [InlineData("\"DISLIKE\"", Rating.Dislike)]
    [InlineData("\"INDIFFERENT\"", Rating.None)]
    [InlineData("\"SOMETHING_NEW\"", null)]
    [InlineData("3", null)]
    public void ParseRating_MapsKnownValuesOnly(string status, Rating? expected)
    {
        using var doc = JsonDocument.Parse(
            """{"playerOverlays":{"playerOverlayRenderer":{"actions":[{"likeButtonRenderer":{"target":{"videoId":"v"},"likeStatus":STATUS}}]}}}""".Replace("STATUS", status));

        Assert.Equal(expected, NextResponseParser.ParseRating(doc.RootElement, "v"));
    }
}
