using System.Text.Json;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// The playlist header's author, year, track count and duration,
/// read the way ytmusicapi's parse_playlist_header / parse_playlist_header_meta read
/// them: author from facepile.avatarStackViewModel (text.content, and the channel
/// from rendererContext.commandContext.onTap.innertubeCommand.browseEndpoint),
/// unless that tap opens the "PAplaylist_collaborate" panel; year from subtitle
/// runs[2]; count and duration from secondSubtitle, with views first when it has
/// more than three runs. The recorded playlist is YTM-made, so its author has no
/// link; the user-made shapes below are hand-written to the same paths.
/// </summary>
public class PlaylistHeaderTests
{
    private static JsonDocument LoadFixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    private static Core.Models.PlaylistPage ParseHeader(string headerJson)
    {
        using var doc = JsonDocument.Parse(
            """{"contents": {"twoColumnBrowseResultsRenderer": {"tabs": [{"tabRenderer": {"content": {"sectionListRenderer": {"contents": [{"musicResponsiveHeaderRenderer": """ +
            headerJson +
            "}]}}}}]}}}");
        return BrowseParsers.ParsePlaylist("PLx", doc.RootElement);
    }

    [Fact]
    public void RecordedHeader_YtmPlaylist()
    {
        using var doc = LoadFixture("browse-playlist.80s-pop.json");

        var page = BrowseParsers.ParsePlaylist("RDCLAK5uy_x", doc.RootElement);

        Assert.Equal("YouTube Music", page.Author?.Name);
        Assert.Null(page.Author?.BrowseId);
        Assert.Equal(2026, page.Year);
        Assert.Equal(101, page.TrackCount);
        Assert.Equal("7+ hours", page.DurationText);
        Assert.False(page.IsEditable);
    }

    /// <summary>
    /// A playlist the account owns (synthetic: it needs a signed-in session). The
    /// shape is ytmusicapi's get_playlist: the header sits under
    /// musicEditablePlaylistDetailHeaderRenderer.header, whose presence is "owned",
    /// and the subtitle names the privacy before the year (runs[4]).
    /// </summary>
    [Fact]
    public void OwnedPlaylist_IsEditable_AndKeepsItsHeader()
    {
        using var doc = LoadFixture("browse-playlist.owned.synthetic.json");

        var page = BrowseParsers.ParsePlaylist("PLxA11yyBB22cc33DD44eeFF55gg66hhII77j", doc.RootElement);

        Assert.True(page.IsEditable);
        Assert.Equal("Late Night Coding", page.Title);
        Assert.Equal("Redacted User", page.Author?.Name);
        Assert.Equal(2026, page.Year);
        Assert.Equal(12, page.TrackCount);
        Assert.Equal(12, page.Tracks.Count);
        Assert.All(page.Tracks, t => Assert.NotNull(t.SetVideoId));
        Assert.Null(page.Continuation);
    }

    [Fact]
    public void UserPlaylist_AuthorLinksTheirChannel_AndViewsComeFirst()
    {
        var page = ParseHeader("""
            {
              "title": {"runs": [{"text": "Late Night Coding"}]},
              "subtitle": {"runs": [{"text": "Playlist"}, {"text": " • "}, {"text": "2023"}]},
              "secondSubtitle": {"runs": [{"text": "12,345 views"}, {"text": " • "}, {"text": "1,204 songs"}, {"text": " • "}, {"text": "80+ hours"}]},
              "facepile": {"avatarStackViewModel": {
                "text": {"content": "misterepicpants"},
                "rendererContext": {"commandContext": {"onTap": {"innertubeCommand": {
                  "browseEndpoint": {"browseId": "UCfJzl9qZ6Zb7mZ2Qqg8jD0A"}}}}}}}
            }
            """);

        Assert.Equal("misterepicpants", page.Author?.Name);
        Assert.Equal("UCfJzl9qZ6Zb7mZ2Qqg8jD0A", page.Author?.BrowseId);
        Assert.Equal(2023, page.Year);
        Assert.Equal(1204, page.TrackCount);
        Assert.Equal("80+ hours", page.DurationText);
    }

    [Fact]
    public void CollaborativePlaylist_HasNoSingleAuthor()
    {
        var page = ParseHeader("""
            {
              "title": {"runs": [{"text": "Road Trip"}]},
              "secondSubtitle": {"runs": [{"text": "3 songs"}]},
              "facepile": {"avatarStackViewModel": {
                "text": {"content": "Alex and 2 others"},
                "rendererContext": {"commandContext": {"onTap": {"innertubeCommand": {
                  "showEngagementPanelEndpoint": {"identifier": {"tag": "PAplaylist_collaborate"}}}}}}}}
            }
            """);

        Assert.Null(page.Author);
        Assert.Equal(3, page.TrackCount);
        Assert.Null(page.DurationText);
        Assert.Null(page.Year);
    }

    [Fact]
    public void StraplineAuthor_IsTheFallback()
    {
        var page = ParseHeader("""
            {
              "title": {"runs": [{"text": "Best Of Daft Punk"}]},
              "straplineTextOne": {"runs": [{"text": "Daft Punk", "navigationEndpoint": {"browseEndpoint": {"browseId": "UCRr1xG_2WIDs18a6cIiCxeA"}}}]}
            }
            """);

        Assert.Equal(new Core.Models.ArtistRef("Daft Punk", "UCRr1xG_2WIDs18a6cIiCxeA"), page.Author);
        Assert.Null(page.TrackCount);
    }

    [Fact]
    public void BareHeader_LeavesEverythingNull()
    {
        var page = ParseHeader("""{"title": {"runs": [{"text": "x"}]}, "subtitle": {"runs": [{"text": "Playlist"}]}}""");

        Assert.Null(page.Author);
        Assert.Null(page.Year);
        Assert.Null(page.TrackCount);
        Assert.Null(page.DurationText);
    }
}
