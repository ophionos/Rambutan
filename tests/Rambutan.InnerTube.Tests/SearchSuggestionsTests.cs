using System.Text.Json;
using Rambutan.Core.Models;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// search-suggestions.daft-punk.synthetic.json is hand-authored (the sandbox that
/// wrote it can't reach YTM). Its first section mirrors what ytmusicapi's
/// parse_search_suggestions reads (searchSuggestionRenderer and
/// historySuggestionRenderer: suggestion.runs, navigationEndpoint.searchEndpoint.query,
/// serviceEndpoint.feedbackEndpoint on history rows); its second is the entity
/// section of musicResponsiveListItemRenderer rows, whose ids, titles and art are
/// copied from the recorded search-*.daft-punk.json fixtures. Replace it with
/// `FixtureRecorder -- suggestions "daft punk"`.
/// </summary>
public class SearchSuggestionsTests
{
    private static SearchSuggestions Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SearchResponseParser.ParseSuggestions(doc.RootElement);
    }

    private static SearchSuggestions ParseFixture(string name) =>
        Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void TextSuggestions_ComeInOrder_HistoryMarked()
    {
        var suggestions = ParseFixture("search-suggestions.daft-punk.synthetic.json");

        Assert.Equal(
            ["daft punk", "daft punk get lucky", "daft punk one more time", "daft punk around the world"],
            suggestions.Queries.Select(q => q.Query));
        Assert.True(suggestions.Queries[0].FromHistory);
        Assert.All(suggestions.Queries.Skip(1), q => Assert.False(q.FromHistory));
    }

    [Fact]
    public void EntitySuggestions_AreTypedEntries()
    {
        var suggestions = ParseFixture("search-suggestions.daft-punk.synthetic.json");

        Assert.Equal(3, suggestions.Entries.Count);

        var artist = Assert.IsType<ArtistEntry>(suggestions.Entries[0]);
        Assert.Equal("Daft Punk", artist.Title);
        Assert.Equal("UCRr1xG_2WIDs18a6cIiCxeA", artist.BrowseId);
        Assert.Equal("Artist", artist.Subtitle);

        // The song row's watch target is on the row itself, not on the title run.
        var song = Assert.IsType<SongEntry>(suggestions.Entries[1]);
        Assert.Equal("khnokW3Mw24", song.Track.VideoId);
        Assert.Equal("Daft Punk", Assert.Single(song.Track.Artists).Name);
        Assert.Equal("UCRr1xG_2WIDs18a6cIiCxeA", song.Track.Artists[0].BrowseId);

        var album = Assert.IsType<AlbumEntry>(suggestions.Entries[2]);
        Assert.Equal("MPREb_K8qWMWVqXGi", album.BrowseId);

        Assert.All(suggestions.Entries, e => Assert.False(string.IsNullOrEmpty(e.ThumbnailUrl)));
    }

    [Fact]
    public void SuggestionWithoutEndpoint_FallsBackToItsText()
    {
        var suggestions = Parse("""
            {"contents": [{"searchSuggestionsSectionRenderer": {"contents": [
              {"searchSuggestionRenderer": {"suggestion": {"runs": [{"text": "daft", "bold": true}, {"text": " punk"}]}}}
            ]}}]}
            """);

        Assert.Equal("daft punk", Assert.Single(suggestions.Queries).Query);
    }

    [Theory]
    [InlineData("""{"responseContext": {}}""")]
    [InlineData("""{"contents": []}""")]
    [InlineData("""{"contents": [{"searchSuggestionsSectionRenderer": {}}]}""")]
    [InlineData("""{"contents": {}}""")]
    public void NoSuggestions_IsEmptyNotThrow(string json)
    {
        var suggestions = Parse(json);

        Assert.Empty(suggestions.Queries);
        Assert.Empty(suggestions.Entries);
    }
}
