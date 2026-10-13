using System.Text.Json;
using Rambutan.Core.Models;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// Moods &amp; genres: the grid of tiles (browse-moods-and-genres.json),
/// a mood's page (Chill), a genre's page (Soundtracks &amp; musicals), the page
/// behind a shelf's "More" (Coffee shop blends), and Home's mood chips
/// (browse-home.json, and browse-home.relax.json filtered to Relax). All recorded
/// signed out.
/// </summary>
public class MoodsAndGenresTests
{
    private static JsonDocument LoadFixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void Grid_HasAGroupOfTilesForMoodsAndOneForGenres()
    {
        using var doc = LoadFixture("browse-moods-and-genres.json");

        var page = BrowseParsers.ParseMoodsAndGenres(doc.RootElement);

        Assert.Equal(["Moods & moments", "Genres"], page.Sections.Select(s => s.Title));
        Assert.Equal(12, page.Sections[0].Categories.Count);
        Assert.Equal(24, page.Sections[1].Categories.Count);

        var chill = page.Sections[0].Categories[0];
        Assert.Equal("Chill", chill.Title);
        Assert.Equal("ggMPOg1uX1JOQWZFeDByc2Jm", chill.Params);
        Assert.Equal(0xFFA4C5FFu, chill.Color);
    }

    [Fact]
    public void Grid_WithoutContents_IsEmpty()
    {
        using var doc = JsonDocument.Parse("{}");

        Assert.Empty(BrowseParsers.ParseMoodsAndGenres(doc.RootElement).Sections);
    }

    [Fact]
    public void MoodPage_IsShelvesOfPlaylists_EachLinkingItsOwnPage()
    {
        using var doc = LoadFixture("browse-moods-category.chill.json");

        var page = BrowseParsers.ParseMoodCategory(doc.RootElement);

        Assert.Equal("Chill", page.Title);
        Assert.Equal(10, page.Shelves.Count);
        Assert.All(page.Shelves, s => Assert.All(s.Entries, e => Assert.IsType<PlaylistEntry>(e)));

        var first = page.Shelves[0];
        Assert.Equal("Coffee shop blends", first.Title);
        Assert.Equal(14, first.Entries.Count);
        Assert.Equal(new MoodCategory("Coffee shop blends", "ggMPOg1uX2dsdVFONkZBREhy"), first.More);
        Assert.All(page.Shelves, s => Assert.NotNull(s.More));
    }

    [Fact]
    public void GenrePage_HasSongs_Playlists_Videos_AndAlbums()
    {
        using var doc = LoadFixture("browse-moods-category.soundtracks.json");

        var page = BrowseParsers.ParseMoodCategory(doc.RootElement);

        Assert.Equal("Soundtracks & musicals", page.Title);
        Assert.Equal(
            ["Songs", "Featured playlists", "Community playlists", "Music videos", "Albums"],
            page.Shelves.Select(s => s.Title));
        Assert.All(page.Shelves[0].Entries, e => Assert.IsType<SongEntry>(e));
        Assert.All(page.Shelves[3].Entries, e => Assert.IsType<SongEntry>(e));
        Assert.All(page.Shelves[4].Entries, e => Assert.IsType<AlbumEntry>(e));
        Assert.All(page.Shelves, s => Assert.Null(s.More));
    }

    [Fact]
    public void MorePage_ReadsItsSpotlightGrid_AsAShelf()
    {
        using var doc = LoadFixture("browse-moods-category.coffee-shop-blends.json");

        var page = BrowseParsers.ParseMoodCategory(doc.RootElement);

        Assert.Equal("Coffee shop blends", page.Title);
        Assert.Equal(["Spotlight", "Featured playlists", "Community playlists"], page.Shelves.Select(s => s.Title));
        Assert.Equal(2, page.Shelves[0].Entries.Count);
    }

    [Fact]
    public void Home_HasItsMoodChips_WithoutPodcasts()
    {
        using var doc = LoadFixture("browse-home.json");

        var page = BrowseParsers.ParseHome(doc.RootElement);

        Assert.Equal(
            ["Relax", "Sleep", "Romance", "Sad", "Energize", "Party", "Feel good", "Workout", "Commute", "Focus"],
            page.Chips.Select(c => c.Title));
        Assert.DoesNotContain(page.Chips, c => c.IsSelected);
    }

    [Fact]
    public void FilteredHome_MarksItsChip_AndPagesOn()
    {
        using var doc = LoadFixture("browse-home.relax.json");

        var page = BrowseParsers.ParseHome(doc.RootElement);

        Assert.Equal("Relax", Assert.Single(page.Chips, c => c.IsSelected).Title);
        Assert.DoesNotContain(page.Chips, c => c.Title == "Podcasts");
        Assert.Equal("Unwind + explore", page.Shelves[0].Title);
        Assert.NotNull(page.Continuation);
    }

    [Theory]
    [InlineData("ggNCSgQIDBADSgQIBxABSgQIBRABSgQIChABSgQIDRABSgQICRABSgQICBABSgQIDhABSgQIBBABSgQIAxABSgQIBhAB", true)]
    [InlineData("ggNCSgQIDBABSgQIBxADSgQIBRABSgQIChABSgQIDRABSgQICRABSgQICBABSgQIDhABSgQIBBABSgQIAxABSgQIBhAB", false)]
    [InlineData("not base64!", false)]
    public void PodcastChip_IsTheOneSelectingId12(string chipParams, bool podcasts) =>
        Assert.Equal(podcasts, BrowseParsers.IsPodcastChip(chipParams));

    [Fact]
    public async Task FilteredHome_SendsTheChipsParams()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);
        handler.Response = _ => "{}";

        await client.GetHomeAsync(new HomeChip("Relax", "RELAX_PARAMS", IsSelected: false));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("browse", sent.Endpoint);
        Assert.Equal("FEmusic_home", sent.Body.GetProperty("browseId").GetString());
        Assert.Equal("RELAX_PARAMS", sent.Body.GetProperty("params").GetString());
    }

    [Fact]
    public async Task CategoryPage_SendsTheCategoryBrowseIdAndParams()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);
        handler.Response = _ => "{}";

        await client.GetMoodCategoryAsync(new MoodCategory("Chill", "CHILL_PARAMS"));
        await client.GetMoodsAndGenresAsync();

        Assert.Equal("FEmusic_moods_and_genres_category", handler.Requests[0].Body.GetProperty("browseId").GetString());
        Assert.Equal("CHILL_PARAMS", handler.Requests[0].Body.GetProperty("params").GetString());
        Assert.Equal("FEmusic_moods_and_genres", handler.Requests[1].Body.GetProperty("browseId").GetString());
        Assert.False(handler.Requests[1].Body.TryGetProperty("params", out _));
    }
}
