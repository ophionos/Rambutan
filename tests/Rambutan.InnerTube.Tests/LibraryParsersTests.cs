using System.Text.Json;
using Rambutan.Core.Models;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// The *.synthetic.json fixtures are hand-authored to the live response shape
/// (library surfaces need auth, so they can't be recorded anonymously). Replace
/// them with recorded + scrubbed captures via tools/Rambutan.FixtureRecorder.
/// </summary>
public class LibraryParsersTests
{
    private static JsonDocument LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void ParseLibraryPlaylists_GridWithNewPlaylistButton_YieldsPlaylistsAndContinuation()
    {
        using var doc = LoadFixture("browse-library-playlists.synthetic.json");

        var page = LibraryParsers.ParseLibraryPlaylists(doc.RootElement);

        // The "New playlist" button contributes nothing; three real playlists remain.
        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, i => Assert.IsType<PlaylistEntry>(i));

        var playlists = page.Items.Cast<PlaylistEntry>().ToList();
        Assert.Equal("Liked Music", playlists[0].Title);
        Assert.Equal("LM", playlists[0].PlaylistId); // de-VL'd, ready for GetPlaylistAsync
        Assert.Equal("Late Night Coding", playlists[1].Title);
        Assert.StartsWith("PL", playlists[1].PlaylistId);
        Assert.All(playlists, p => Assert.False(string.IsNullOrEmpty(p.ThumbnailUrl)));

        Assert.NotNull(page.Continuation);
    }

    [Fact]
    public void ParseLibraryPlaylists_Recorded_TellsOwnPlaylistsFromSavedOnes()
    {
        // A recorded library (own playlists scrubbed): the user's own offer Edit
        // playlist in their menu, saved ones the library toggle, and the auto
        // playlists neither.
        using var doc = LoadFixture("browse-library-playlists.json");

        var playlists = LibraryParsers.ParseLibraryPlaylists(doc.RootElement).Items
            .Cast<PlaylistEntry>().ToList();

        Assert.Equal(10, playlists.Count);
        Assert.Equal(
            ["LM", "SE"],
            playlists.Where(p => p.IsOwn is null).Select(p => p.PlaylistId));
        Assert.Equal(
            ["PLOLBWlPQqPksS974k1cqCJ7FRFYP1cznV", "RDCLAK5uy_kL-nWMqnRoPuUCh9toq5gatFti1kFokH0"],
            playlists.Where(p => p.IsOwn == false).Select(p => p.PlaylistId));
        Assert.Equal(
            Enumerable.Range(1, 6).Select(n => $"My playlist {n}"),
            playlists.Where(p => p.IsOwn == true).Select(p => p.Title));
    }

    [Fact]
    public void ParseLibraryPlaylistsContinuation_GridContinuation_YieldsMoreAndEnds()
    {
        using var doc = LoadFixture("browse-library-playlists-continuation.synthetic.json");

        var page = LibraryParsers.ParseLibraryPlaylistsContinuation(doc.RootElement);

        Assert.Equal(2, page.Items.Count);
        Assert.Null(page.Continuation);
        Assert.Equal("Road Trip", page.Items[0].Title);
    }

    [Fact]
    public void ParseLibraryPlaylists_UnrecognizedShape_YieldsEmptyPageNotThrow()
    {
        using var doc = JsonDocument.Parse("""{"contents": {}}""");

        var page = LibraryParsers.ParseLibraryPlaylists(doc.RootElement);

        Assert.Empty(page.Items);
        Assert.Null(page.Continuation);
    }

    [Fact]
    public void ParseCreatedPlaylistId_ReturnsId()
    {
        using var doc = JsonDocument.Parse(
            """{"responseContext": {}, "playlistId": "PLnew123abc"}""");

        Assert.Equal("PLnew123abc", LibraryParsers.ParseCreatedPlaylistId(doc.RootElement));
    }

    [Fact]
    public void ParseCreatedPlaylistId_MissingId_Throws()
    {
        using var doc = JsonDocument.Parse("""{"responseContext": {}}""");

        Assert.Throws<InvalidOperationException>(
            () => LibraryParsers.ParseCreatedPlaylistId(doc.RootElement));
    }

    [Theory]
    [InlineData("""{"status": "STATUS_SUCCEEDED", "responseContext": {}}""", true)]
    [InlineData("""{"status": "STATUS_FAILED", "responseContext": {}}""", false)]
    [InlineData("""{"responseContext": {}}""", false)]
    public void IsEditSuccess_ChecksStatus(string json, bool expected)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(expected, LibraryParsers.IsEditSuccess(doc.RootElement));
    }

    // Hand-authored from the documented response shape (ytmusicapi's
    // add_playlist_items reads the same path). Editing needs a signed-in session,
    // so there's no recording to replace it with yet.
    [Fact]
    public void ParseAddedSetVideoId_ReadsTheNewRow()
    {
        using var doc = JsonDocument.Parse("""
            {
              "responseContext": {},
              "status": "STATUS_SUCCEEDED",
              "playlistEditResults": [
                { "playlistEditVideoAddedResultData": { "videoId": "dQw4w9WgXcQ", "setVideoId": "56B44F6D10557CC6" } }
              ]
            }
            """);

        Assert.Equal("56B44F6D10557CC6", LibraryParsers.ParseAddedSetVideoId(doc.RootElement));
    }

    [Theory]
    [InlineData("""{"status": "STATUS_SUCCEEDED"}""")]
    [InlineData("""{"status": "STATUS_SUCCEEDED", "playlistEditResults": [{}]}""")]
    [InlineData("""{"status": "STATUS_SUCCEEDED", "playlistEditResults": {}}""")]
    public void ParseAddedSetVideoId_MissingResult_IsNull(string json)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Null(LibraryParsers.ParseAddedSetVideoId(doc.RootElement));
    }
}
