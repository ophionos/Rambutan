using System.Text.Json;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// Unfiltered search, pinned by search-all.daft-punk.json, recorded
/// with `FixtureRecorder -- search "daft punk"` (no --filter). Live YTM sends the top
/// result card, then one itemSectionRenderer per result with the types mixed; the
/// titled per-type shelves ytmusicapi expects are gone.
/// </summary>
public class SearchOverviewTests
{
    private static SearchOverview Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SearchResponseParser.ParseOverview(doc.RootElement);
    }

    private static SearchOverview ParseFixture() => Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "search-all.daft-punk.json")));

    [Fact]
    public void TopResult_IsTheArtistCard_WithItsSongs()
    {
        var top = ParseFixture().TopResult;

        Assert.NotNull(top);
        var artist = Assert.IsType<ArtistEntry>(top!.Entry);
        Assert.Equal("Daft Punk", artist.Title);
        Assert.Equal("UCRr1xG_2WIDs18a6cIiCxeA", artist.BrowseId);
        Assert.Equal("Artist • 85.8M monthly audience", artist.Subtitle);
        Assert.EndsWith("w120-h120-p-l90-rj", artist.ThumbnailUrl);

        Assert.Equal(
            ["fa5IWHDbftI", "ZFZM6jDTWd4", "4D7u5KF7SP8"],
            top.Related.Select(e => Assert.IsType<SongEntry>(e).Track.VideoId));
    }

    [Fact]
    public void AnArtistCardsSongs_AreCreditedToTheArtist()
    {
        // YTM leaves the artist off these rows ("Song • 5:21"): the card is the artist.
        var song = Assert.IsType<SongEntry>(ParseFixture().TopResult!.Related[0]);

        Assert.Equal(new ArtistRef("Daft Punk", "UCRr1xG_2WIDs18a6cIiCxeA"), Assert.Single(song.Track.Artists));
        Assert.Equal("Daft Punk", song.Subtitle);
    }

    [Fact]
    public void Results_KeepTheServersMixedOrder()
    {
        var results = ParseFixture().Results;

        Assert.Equal(23, results.Count);
        Assert.Equal("MPREb_K8qWMWVqXGi", Assert.IsType<AlbumEntry>(results[0]).BrowseId);
        Assert.Equal("Album • Daft Punk • 2013", results[0].Subtitle);
        Assert.Equal("khnokW3Mw24", Assert.IsType<SongEntry>(results[2]).Track.VideoId);
        Assert.Equal("UClYV6hHlupm_S_ObS1W-DYw", Assert.IsType<ArtistEntry>(results[7]).BrowseId);
        Assert.Equal("PLIKihgj0kero", Assert.IsType<PlaylistEntry>(results[9]).PlaylistId);
        Assert.Equal("Indie Disco", Assert.IsType<PlaylistEntry>(results[^1]).Title);
    }

    [Fact]
    public void SongRows_TakeArtistsFromTheirLinks_NotTheTypeLabel()
    {
        var songs = ParseFixture().Results.OfType<SongEntry>().ToList();

        Assert.Equal(
            ["Daft Punk", "Julian Casablancas"],
            songs.Single(s => s.Track.VideoId == "khnokW3Mw24").Track.Artists.Select(a => a.Name));
        Assert.DoesNotContain(songs, s => s.Track.Artists.Any(a => a.Name is "Song" or "Video"));
    }

    [Fact]
    public void PodcastEpisodesAndShows_AreLeftOut_ProfilesStay()
    {
        var results = ParseFixture().Results;

        Assert.DoesNotContain(results, e => e.Title.Contains("Bangalter on Life", StringComparison.Ordinal));
        Assert.DoesNotContain(results, e => e.Title == "The A24 Podcast");
        Assert.Contains(results, e => e is ArtistEntry { BrowseId: "UC8jT0e6m7d_1YJGbD2KY3AA" });
    }

    [Fact]
    public void SongCard_PlaysFromOnTap_WithArtistsAndAlbumFromSubtitle()
    {
        var overview = Parse("""
            {"contents": {"tabbedSearchResultsRenderer": {"tabs": [{"tabRenderer": {"content": {"sectionListRenderer": {"contents": [
              {"musicCardShelfRenderer": {
                "title": {"runs": [{"text": "Get Lucky"}]},
                "subtitle": {"runs": [
                  {"text": "Song"}, {"text": " • "},
                  {"text": "Daft Punk", "navigationEndpoint": {"browseEndpoint": {"browseId": "UCRr1xG_2WIDs18a6cIiCxeA",
                    "browseEndpointContextSupportedConfigs": {"browseEndpointContextMusicConfig": {"pageType": "MUSIC_PAGE_TYPE_ARTIST"}}}}},
                  {"text": " • "},
                  {"text": "Random Access Memories", "navigationEndpoint": {"browseEndpoint": {"browseId": "MPREb_K8qWMWVqXGi",
                    "browseEndpointContextSupportedConfigs": {"browseEndpointContextMusicConfig": {"pageType": "MUSIC_PAGE_TYPE_ALBUM"}}}}},
                  {"text": " • "}, {"text": "6:10"}]},
                "contents": [{"messageRenderer": {"text": {"runs": [{"text": "More from YouTube"}]}}}],
                "onTap": {"watchEndpoint": {"videoId": "5NV6Rdv1a3I"}}
              }}
            ]}}}}]}}}
            """);

        var song = Assert.IsType<SongEntry>(overview.TopResult!.Entry);
        Assert.Equal("5NV6Rdv1a3I", song.Track.VideoId);
        Assert.Equal("Daft Punk", Assert.Single(song.Track.Artists).Name);
        Assert.Equal("MPREb_K8qWMWVqXGi", song.Track.AlbumBrowseId);
        Assert.Equal(TimeSpan.FromSeconds(370), song.Track.Duration);
        Assert.Empty(overview.TopResult.Related);
        Assert.Empty(overview.Results);
    }

    [Fact]
    public void AlbumCard_WithoutTitleLink_UsesOnTap()
    {
        var overview = Parse("""
            {"contents": {"tabbedSearchResultsRenderer": {"tabs": [{"tabRenderer": {"content": {"sectionListRenderer": {"contents": [
              {"musicCardShelfRenderer": {
                "title": {"runs": [{"text": "Discovery"}]},
                "subtitle": {"runs": [{"text": "Album"}, {"text": " • "}, {"text": "Daft Punk"}]},
                "onTap": {"browseEndpoint": {"browseId": "MPREb_discovery",
                  "browseEndpointContextSupportedConfigs": {"browseEndpointContextMusicConfig": {"pageType": "MUSIC_PAGE_TYPE_ALBUM"}}}}
              }}
            ]}}}}]}}}
            """);

        Assert.Equal("MPREb_discovery", Assert.IsType<AlbumEntry>(overview.TopResult!.Entry).BrowseId);
    }

    [Theory]
    [InlineData("""{"responseContext": {}}""")]
    [InlineData("""{"contents": {"tabbedSearchResultsRenderer": {"tabs": []}}}""")]
    [InlineData("""{"contents": {"tabbedSearchResultsRenderer": {"tabs": [{"tabRenderer": {"content": {"sectionListRenderer": {"contents": [{"musicCardShelfRenderer": {}}, {"itemSectionRenderer": {}}, {"itemSectionRenderer": {"contents": [{"messageRenderer": {}}]}}]}}}}]}}}""")]
    public void NoResults_IsEmptyNotThrow(string json)
    {
        var overview = Parse(json);

        Assert.Null(overview.TopResult);
        Assert.Empty(overview.Results);
    }

    [Fact]
    public async Task Request_SendsNoParams()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);
        handler.Response = _ => "{}";

        Assert.Same(SearchOverview.Empty, await client.SearchAllAsync("daft punk"));

        var body = Assert.Single(handler.Requests).Body;
        Assert.Equal("daft punk", body.GetProperty("query").GetString());
        Assert.False(body.TryGetProperty("params", out _));
    }
}
