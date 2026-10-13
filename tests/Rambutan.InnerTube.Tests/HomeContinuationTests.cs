using System.Text.Json;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// Home's later shelves. YTM sends a few shelves per page and a
/// nextContinuationData token for the next few; signed in, a Home runs to about
/// seven pages. browse-home-continuation.json is a recorded signed-out last page
/// (no token). browse-home-continuation.synthetic.json mirrors a signed-in middle
/// page: the same continuationContents.sectionListContinuation shape, with shelves
/// copied from an anonymous recording (a real one is the account's own taste) and a
/// hand-made podcast shelf.
/// </summary>
public class HomeContinuationTests
{
    private static JsonDocument LoadFixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void FirstPage_CarriesTheTokenForMoreShelves()
    {
        using var doc = LoadFixture("browse-home.json");

        var page = BrowseParsers.ParseHome(doc.RootElement);

        Assert.Equal(["Take it easy", "All hits", "Party music"], page.Shelves.Select(s => s.Title));
        Assert.StartsWith("4qmFsgKhAhIMRkVtdXNpY19ob21l", page.Continuation?.Value);
    }

    [Fact]
    public void MiddlePage_YieldsShelvesAndTheNextToken()
    {
        using var doc = LoadFixture("browse-home-continuation.synthetic.json");

        var page = BrowseParsers.ParseHomeContinuation(doc.RootElement);

        // "Shows for you" holds only a podcast, which has no page here to open.
        Assert.Equal(["Quick picks", "New releases"], page.Shelves.Select(s => s.Title));
        Assert.All(page.Shelves, s => Assert.Equal(3, s.Entries.Count));
        Assert.Equal("synthetic-home-page-3", page.Continuation?.Value);
    }

    [Fact]
    public void LastPage_HasShelvesAndNoToken()
    {
        using var doc = LoadFixture("browse-home-continuation.json");

        var page = BrowseParsers.ParseHomeContinuation(doc.RootElement);

        var shelf = Assert.Single(page.Shelves);
        Assert.Equal("Featured playlists for you", shelf.Title);
        Assert.Equal(5, shelf.Entries.Count);
        Assert.Null(page.Continuation);
    }

    [Fact]
    public void AnEmptyTab_IsTheEnd()
    {
        // What YTM answers a token it won't continue: the Home tab with no content
        // (a signed-out request without the visitor, or the body form of the token).
        using var doc = JsonDocument.Parse("""
            {"contents":{"singleColumnBrowseResultsRenderer":{"tabs":[{"tabRenderer":{"title":"Home"}}]}}}
            """);

        var page = BrowseParsers.ParseHomeContinuation(doc.RootElement);

        Assert.Empty(page.Shelves);
        Assert.Null(page.Continuation);
    }

    [Fact]
    public async Task Continuation_SendsTheTokenInTheQuery_AndTheHomeVisitor()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);
        handler.Response = sent => sent.Query.Contains("ctoken=", StringComparison.Ordinal)
            ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "browse-home-continuation.json"))
            : File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "browse-home.json"));

        var first = await client.GetHomeAsync();
        var more = await client.GetHomeContinuationAsync(first.Continuation!);

        var sent = handler.Requests[1];
        Assert.Equal("browse", sent.Endpoint);
        Assert.Contains($"ctoken={first.Continuation!.Value}&continuation={first.Continuation.Value}&type=next", sent.Query);
        Assert.False(sent.Body.TryGetProperty("continuation", out _));
        Assert.False(sent.Body.TryGetProperty("browseId", out _));

        using var home = LoadFixture("browse-home.json");
        Assert.Equal(
            home.RootElement.GetProperty("responseContext").GetProperty("visitorData").GetString(),
            sent.Body.GetProperty("context").GetProperty("client").GetProperty("visitorData").GetString());
        Assert.Single(more.Shelves);
    }

    [Fact]
    public async Task OtherRequests_DoNotCarryAVisitor()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);
        handler.Response = _ => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "browse-home.json"));

        await client.GetHomeAsync();
        await client.GetHomeAsync();

        Assert.All(handler.Requests, r =>
            Assert.False(r.Body.GetProperty("context").GetProperty("client").TryGetProperty("visitorData", out _)));
    }
}
