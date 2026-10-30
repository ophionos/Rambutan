using System.Text.Json;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// The account's listening history. browse-history.synthetic.json is
/// hand-authored to the shape of a signed-in recording of FEmusic_history
/// (October 2026): one musicShelfRenderer per period, no continuation, each row
/// menu with a "Remove from history" item (icon REMOVE_FROM_HISTORY) followed by
/// "Pin to Listen again". Its songs are the public Daft Punk rows of the search
/// fixtures, not the recorded account's, and its tokens are placeholders. The
/// removal's payload mirrors ytmusicapi's remove_history_items.
/// </summary>
public class HistoryTests
{
    private static JsonDocument LoadFixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    private static List<HistoryEntry> Entries()
    {
        using var doc = LoadFixture("browse-history.synthetic.json");
        return LibraryParsers.ParseHistory(doc.RootElement).Items
            .Select(i => Assert.IsType<HistoryEntry>(i)).ToList();
    }

    [Fact]
    public void History_IsEveryShelfsSongs_UnderTheShelfsTitle_InOrder()
    {
        var entries = Entries();

        Assert.Equal(
            [
                ("khnokW3Mw24", "Today"), ("wU26xVT_vBU", "Today"),
                ("FGBhQbmPwH8", "Yesterday"), ("Jb6gcoR266U", "Yesterday"),
                ("qe8Q7mjxjig", "October 2026"),
            ],
            entries.Select(e => (e.Track.VideoId, e.Played)));
    }

    [Fact]
    public void History_HasNoNextPage()
    {
        using var doc = LoadFixture("browse-history.synthetic.json");

        Assert.Null(LibraryParsers.ParseHistory(doc.RootElement).Continuation);
    }

    [Fact]
    public void A_song_row_is_read_as_any_song_row()
    {
        var first = Entries()[0];

        Assert.Equal("Instant Crush (feat. Julian Casablancas)", first.Title);
        Assert.Equal("Daft Punk", first.Subtitle);
        Assert.Equal("Random Access Memories", first.Track.Album);
        Assert.Equal("MPREb_K8qWMWVqXGi", first.Track.AlbumBrowseId);
        Assert.Equal(TimeSpan.FromSeconds(5 * 60 + 38), first.Track.Duration);
        Assert.Equal(new LibraryToggle(false, "library-add-khnokW3Mw24", "library-remove-khnokW3Mw24"), first.Track.Library);
    }

    [Fact]
    public void A_video_row_has_no_album()
    {
        var video = Entries()[2];

        Assert.Equal("Daft Punk - One More Time (Official Video)", video.Title);
        Assert.Null(video.Track.Album);
        Assert.Equal(TimeSpan.FromSeconds(5 * 60 + 22), video.Track.Duration);
    }

    [Fact]
    public void The_remove_token_is_the_items_with_that_icon_not_the_last_items()
    {
        var entries = Entries();

        Assert.Equal("history-remove-khnokW3Mw24", entries[0].RemoveToken);
        Assert.Equal("history-remove-FGBhQbmPwH8", entries[2].RemoveToken);
        // The last row's menu has no removal; Pin to Listen again's token isn't it.
        Assert.Null(entries[4].RemoveToken);
    }

    [Fact]
    public void A_paused_history_has_no_entries()
    {
        using var doc = JsonDocument.Parse("""
            {"contents": {"singleColumnBrowseResultsRenderer": {"tabs": [{"tabRenderer": {"content": {"sectionListRenderer": {
              "contents": [{"musicNotifierShelfRenderer": {"title": {"runs": [{"text": "Your history is paused"}]}}}]
            }}}}]}}}
            """);

        var page = LibraryParsers.ParseHistory(doc.RootElement);

        Assert.Empty(page.Items);
        Assert.Null(page.Continuation);
    }

    [Fact]
    public void An_unknown_shape_has_no_entries()
    {
        using var doc = JsonDocument.Parse("""{"contents": {}}""");

        Assert.Empty(LibraryParsers.ParseHistory(doc.RootElement).Items);
    }

    [Fact]
    public async Task GetHistory_browses_the_history()
    {
        var (client, handler) = RecordingHandler.Client();
        handler.Response = _ => """{"contents": {}}""";

        await client.GetHistoryAsync();

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("browse", sent.Endpoint);
        Assert.Equal("FEmusic_history", sent.Body.GetProperty("browseId").GetString());
    }

    [Fact]
    public async Task RemoveFromHistory_sends_the_token_as_feedback()
    {
        var (client, handler) = RecordingHandler.Client();
        handler.Response = _ => """{"feedbackResponses":[{"isProcessed":true}]}""";

        await client.RemoveFromHistoryAsync("history-remove-khnokW3Mw24");

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("feedback", sent.Endpoint);
        Assert.Equal(["history-remove-khnokW3Mw24"],
            sent.Body.GetProperty("feedbackTokens").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task RemoveFromHistory_not_processed_throws()
    {
        var (client, handler) = RecordingHandler.Client();
        handler.Response = _ => """{"feedbackResponses":[{"isProcessed":false}]}""";

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RemoveFromHistoryAsync("T"));
    }

    [Fact]
    public async Task SignedOut_history_calls_throw_without_a_request()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);

        await Assert.ThrowsAsync<NotSignedInException>(() => client.GetHistoryAsync());
        await Assert.ThrowsAsync<NotSignedInException>(() => client.RemoveFromHistoryAsync("T"));
        Assert.Empty(handler.Requests);
    }
}
