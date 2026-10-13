using System.Text.Json;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// Library songs and the per-song "Save to library" toggle. The
/// browse-library-songs* fixtures are hand-authored: the surface needs a signed-in
/// session. They follow ytmusicapi's parse_library_songs (sectionListRenderer →
/// itemSectionRenderer → musicShelfRenderer, a leading "Shuffle all" row, paged by
/// "musicShelfContinuation"). The song rows are real rows from
/// browse-playlist.80s-pop.json, without their playlist ids, each given the
/// toggleMenuServiceItemRenderer that parse_song_menu_tokens reads; the tokens are
/// placeholders. The mutation payload mirrors edit_song_library_status. Replace the
/// fixtures with the recorder commands in Fixtures/README.md.
/// </summary>
public class LibrarySongsTests
{
    private static JsonDocument LoadFixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void LibrarySongs_ShelfRows_AreSongsInTheLibrary()
    {
        using var doc = LoadFixture("browse-library-songs.synthetic.json");

        var page = LibraryParsers.ParseLibrarySongs(doc.RootElement);

        // 25 songs; the "Shuffle all" row has no videoId and isn't one.
        Assert.Equal(25, page.Items.Count);
        var songs = page.Items.Select(i => Assert.IsType<SongEntry>(i)).ToList();
        var first = songs[0].Track;
        Assert.Equal("lcOxhH8N3Bo", first.VideoId);
        Assert.Equal("Total Eclipse of the Heart (Turn Around)", first.Title);
        Assert.Equal("Bonnie Tyler", first.ArtistDisplay);
        Assert.Equal(TimeSpan.FromSeconds(5 * 60 + 34), first.Duration);
        Assert.Null(first.SetVideoId);   // not a playlist row
        Assert.Equal(new LibraryToggle(true, "SYNTH_ADD_lcOxhH8N3Bo", "SYNTH_REMOVE_lcOxhH8N3Bo"), first.Library);
        Assert.All(songs, s => Assert.True(s.Track.Library?.InLibrary));
        Assert.Equal("SYNTH_LIBRARY_SONGS_PAGE_2", page.Continuation?.Value);
    }

    [Fact]
    public void LibrarySongs_Continuation_YieldsTheRestAndEnds()
    {
        using var doc = LoadFixture("browse-library-songs-continuation.synthetic.json");

        var page = LibraryParsers.ParseLibrarySongsContinuation(doc.RootElement);

        Assert.Equal(6, page.Items.Count);
        Assert.All(page.Items, i => Assert.NotNull(Assert.IsType<SongEntry>(i).Track.Library));
        Assert.Null(page.Continuation);
    }

    [Fact]
    public void LibrarySongs_AppendActionsShape_IsReadToo()
    {
        using var doc = JsonDocument.Parse("""
            {"onResponseReceivedActions": [{"appendContinuationItemsAction": {"continuationItems": [
              {"musicResponsiveListItemRenderer": {
                "flexColumns": [{"musicResponsiveListItemFlexColumnRenderer": {"text": {"runs": [
                  {"text": "Genesis", "navigationEndpoint": {"watchEndpoint": {"videoId": "vid1"}}}]}}}]}},
              {"continuationItemRenderer": {"continuationEndpoint": {"continuationCommand": {"token": "MORE"}}}}
            ]}}]}
            """);

        var page = LibraryParsers.ParseLibrarySongsContinuation(doc.RootElement);

        Assert.Equal("vid1", Assert.IsType<SongEntry>(Assert.Single(page.Items)).Track.VideoId);
        Assert.Equal("MORE", page.Continuation?.Value);
    }

    [Fact]
    public void EmptyLibrary_HasNoSongs()
    {
        using var doc = JsonDocument.Parse("""{"contents": {}}""");

        Assert.Empty(LibraryParsers.ParseLibrarySongs(doc.RootElement).Items);
    }

    /// <summary>The toggle a song row with this menu item parses to, read through
    /// a continuation page as any list row is.</summary>
    private static LibraryToggle? ToggleOf(string menuItem)
    {
        using var doc = JsonDocument.Parse(
            """
            {"continuationContents": {"musicShelfContinuation": {"contents": [{"musicResponsiveListItemRenderer": {
              "flexColumns": [{"musicResponsiveListItemFlexColumnRenderer": {"text": {"runs": [
                {"text": "Song", "navigationEndpoint": {"watchEndpoint": {"videoId": "vid1"}}}]}}}],
              "menu": {"menuRenderer": {"items": [{"menuServiceItemRenderer": {"icon": {"iconType": "QUEUE_PLAY_NEXT"}}},
            """ + menuItem + "]}}}}]}}}");
        var page = LibraryParsers.ParseLibrarySongsContinuation(doc.RootElement);
        return Assert.IsType<SongEntry>(Assert.Single(page.Items)).Track.Library;
    }

    private static string Toggle(string icon, bool? isToggled = null)
    {
        var state = isToggled switch { true => ", \"isToggled\": true", false => ", \"isToggled\": false", _ => "" };
        return $$$"""
            {"toggleMenuServiceItemRenderer": {
              "defaultIcon": {"iconType": "{{{icon}}}"},
              "defaultServiceEndpoint": {"feedbackEndpoint": {"feedbackToken": "DEFAULT"}},
              "toggledServiceEndpoint": {"feedbackEndpoint": {"feedbackToken": "TOGGLED"}}{{{state}}}
            """ + "}}";
    }

    [Theory]
    // The current shape: the icon stays the "add" one and isToggled carries the state.
    [InlineData("BOOKMARK_BORDER", false, false, "DEFAULT", "TOGGLED")]
    [InlineData("BOOKMARK_BORDER", true, true, "DEFAULT", "TOGGLED")]
    [InlineData("BOOKMARK_BORDER", null, false, "DEFAULT", "TOGGLED")]
    // The swapped shape: a "remove" icon means saved, and its default endpoint removes.
    [InlineData("BOOKMARK", null, true, "TOGGLED", "DEFAULT")]
    [InlineData("LIBRARY_ADD", null, false, "DEFAULT", "TOGGLED")]
    [InlineData("LIBRARY_SAVED", null, true, "TOGGLED", "DEFAULT")]
    public void LibraryToggle_ReadsStateAndTokens(
        string icon, bool? isToggled, bool inLibrary, string add, string remove)
    {
        var toggle = ToggleOf(Toggle(icon, isToggled));

        Assert.Equal(new LibraryToggle(inLibrary, add, remove), toggle);
    }

    [Fact]
    public void LibraryToggle_OtherToggles_AreNotIt()
    {
        // Pin to Listen again is a toggle item too.
        Assert.Null(ToggleOf(Toggle("KEEP")));
    }

    [Fact]
    public void LibraryToggle_MissingToken_IsUnknown()
    {
        var row = """
            {"toggleMenuServiceItemRenderer": {
              "defaultIcon": {"iconType": "BOOKMARK_BORDER"},
              "defaultServiceEndpoint": {"feedbackEndpoint": {"feedbackToken": "DEFAULT"}}}}
            """;

        Assert.Null(ToggleOf(row));
    }

    [Fact]
    public void SignedOutRows_CarryNoToggle()
    {
        using var doc = LoadFixture("browse-playlist.80s-pop.json");

        var page = BrowseParsers.ParsePlaylist("VLRDCLAK5uy_k1Wu8QbZASiGVqr1wmie9NIYo38aBqscQ", doc.RootElement);

        Assert.NotEmpty(page.Tracks);
        Assert.All(page.Tracks, t => Assert.Null(t.Library));
    }

    [Theory]
    [InlineData(true, "ADD_TOKEN")]
    [InlineData(false, "REMOVE_TOKEN")]
    public async Task SetSongInLibrary_SendsTheMatchingToken(bool save, string token)
    {
        var (client, handler) = RecordingHandler.Client();
        handler.Response = _ => """{"feedbackResponses":[{"isProcessed":true}]}""";

        await client.SetSongInLibraryAsync(new LibraryToggle(!save, "ADD_TOKEN", "REMOVE_TOKEN"), save);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("feedback", sent.Endpoint);
        Assert.Equal([token],
            sent.Body.GetProperty("feedbackTokens").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task SetSongInLibrary_NotProcessed_Throws()
    {
        var (client, handler) = RecordingHandler.Client();
        handler.Response = _ => """{"feedbackResponses":[{"isProcessed":false}]}""";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SetSongInLibraryAsync(new LibraryToggle(false, "A", "R"), true));
    }

    [Theory]
    [InlineData("""{}""", null)]
    [InlineData("""{"feedbackResponses":[{"isProcessed":true}]}""", true)]
    [InlineData("""{"feedbackResponses":[{"isProcessed":true},{"isProcessed":false}]}""", false)]
    public void FeedbackProcessed_IsReadFromEveryResponse(string json, bool? expected)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(expected, LibraryParsers.IsFeedbackProcessed(doc.RootElement));
    }

    [Fact]
    public async Task SignedOut_SongCalls_ThrowWithoutARequest()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);

        await Assert.ThrowsAsync<NotSignedInException>(() => client.GetLibrarySongsAsync());
        await Assert.ThrowsAsync<NotSignedInException>(() =>
            client.GetLibrarySongsContinuationAsync(new ContinuationToken("T")));
        await Assert.ThrowsAsync<NotSignedInException>(() =>
            client.SetSongInLibraryAsync(new LibraryToggle(false, "A", "R"), true));
        Assert.Empty(handler.Requests);
    }
}
