using System.Text.Json;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// Filtered-search paging. The first page is the recorded
/// search-songs.daft-punk.json. search-songs-continuation.daft-punk.synthetic.json
/// is hand-authored (the sandbox couldn't reach YTM) to the shape ytmusicapi's
/// search reads through get_continuations(res["musicShelfRenderer"],
/// "musicShelfContinuation", …): continuationContents.musicShelfContinuation.contents,
/// with real rows copied from the recorded videos search so they don't repeat page
/// one. It has no `continuations`, as the last page of a search has none. Replace it
/// with `FixtureRecorder -- search "daft punk" --filter songs --continuation <token>`.
/// </summary>
public class SearchContinuationTests
{
    private static JsonDocument LoadFixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void FirstPage_CarriesTheShelfToken()
    {
        using var doc = LoadFixture("search-songs.daft-punk.json");

        var page = SearchResponseParser.ParsePage(doc.RootElement);

        Assert.Equal(20, page.Items.Count);
        Assert.NotNull(page.Continuation);
        Assert.StartsWith("EqYDEglkYWZ0IHB1bmsa", page.Continuation!.Value);
    }

    [Fact]
    public void ContinuationPage_YieldsRowsAndEnds()
    {
        using var doc = LoadFixture("search-songs-continuation.daft-punk.synthetic.json");

        var page = SearchResponseParser.ParseContinuation(doc.RootElement);

        Assert.Equal(10, page.Items.Count);
        var first = Assert.IsType<SongEntry>(page.Items[0]);
        Assert.Equal("FGBhQbmPwH8", first.Track.VideoId);
        Assert.Equal("Daft Punk", first.Track.Artists[0].Name);
        Assert.Null(page.Continuation);
    }

    [Fact]
    public void ContinuationPage_ShelfToken_IsNextPage()
    {
        using var doc = JsonDocument.Parse("""
            {"continuationContents": {"musicShelfContinuation": {
              "contents": [],
              "continuations": [{"nextContinuationData": {"continuation": "PAGE3"}}]}}}
            """);

        Assert.Equal("PAGE3", SearchResponseParser.ParseContinuation(doc.RootElement).Continuation?.Value);
    }

    [Fact]
    public void ContinuationPage_AppendActionsShape_IsReadToo()
    {
        // The 2025 shape (ytmusicapi's get_continuations_2025): appended items with a
        // trailing continuationItemRenderer.
        using var doc = JsonDocument.Parse("""
            {"onResponseReceivedActions": [{"appendContinuationItemsAction": {"continuationItems": [
              {"continuationItemRenderer": {"continuationEndpoint": {"continuationCommand": {"token": "NEXT"}}}}
            ]}}]}
            """);

        var page = SearchResponseParser.ParseContinuation(doc.RootElement);

        Assert.Empty(page.Items);
        Assert.Equal("NEXT", page.Continuation?.Value);
    }

    [Theory]
    [InlineData("""{"responseContext": {}}""")]
    [InlineData("""{"continuationContents": {}}""")]
    [InlineData("""{"onResponseReceivedActions": {}}""")]
    public void ContinuationPage_UnknownShape_IsEmptyNotThrow(string json)
    {
        using var doc = JsonDocument.Parse(json);

        var page = SearchResponseParser.ParseContinuation(doc.RootElement);

        Assert.Empty(page.Items);
        Assert.Null(page.Continuation);
    }

    [Fact]
    public async Task ContinuationRequest_RepeatsTheQueryAndPassesTheToken()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);
        handler.Response = _ => """{"continuationContents": {"musicShelfContinuation": {"contents": []}}}""";

        var page = await client.GetSearchContinuationAsync(
            "daft punk", SearchFilter.Albums, new ContinuationToken("TOKEN"));

        Assert.Empty(page.Items);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("search", sent.Endpoint);
        Assert.Contains("ctoken=TOKEN&continuation=TOKEN", sent.Query);
        Assert.Equal("daft punk", sent.Body.GetProperty("query").GetString());
        Assert.StartsWith("EgWKAQIY", sent.Body.GetProperty("params").GetString());
    }
}
