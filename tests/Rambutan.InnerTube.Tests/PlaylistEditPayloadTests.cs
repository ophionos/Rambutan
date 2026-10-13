using Rambutan.Core.Catalog;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// browse/edit_playlist request bodies for reordering, pinned against
/// ytmusicapi's edit_playlist(moveItem=(setVideoId, successorSetVideoId)):
/// one ACTION_MOVE_VIDEO_BEFORE naming the moved row and, unless it goes last,
/// the row it lands before. Editing needs a signed-in session, so there is no
/// recording; the response is the documented STATUS_SUCCEEDED.
/// </summary>
public class PlaylistEditPayloadTests
{
    [Fact]
    public async Task Move_SendsMoveBeforeWithTheSuccessor()
    {
        var (client, handler) = RecordingHandler.Client();

        await client.MovePlaylistItemAsync("VLPLabc", "SET_C", "SET_A");

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("browse/edit_playlist", sent.Endpoint);
        Assert.Equal("PLabc", sent.Body.GetProperty("playlistId").GetString());   // VL stripped
        var action = Assert.Single(sent.Body.GetProperty("actions").EnumerateArray().ToArray());
        Assert.Equal("ACTION_MOVE_VIDEO_BEFORE", action.GetProperty("action").GetString());
        Assert.Equal("SET_C", action.GetProperty("setVideoId").GetString());
        Assert.Equal("SET_A", action.GetProperty("movedSetVideoIdSuccessor").GetString());
        Assert.Equal(3, action.EnumerateObject().Count());
    }

    [Fact]
    public async Task Move_ToTheEnd_OmitsTheSuccessor()
    {
        var (client, handler) = RecordingHandler.Client();

        await client.MovePlaylistItemAsync("PLabc", "SET_A", beforeSetVideoId: null);

        var action = handler.Requests[0].Body.GetProperty("actions")[0];
        Assert.Equal("SET_A", action.GetProperty("setVideoId").GetString());
        Assert.False(action.TryGetProperty("movedSetVideoIdSuccessor", out _));
    }

    [Fact]
    public async Task Move_Rejected_Throws()
    {
        var (client, handler) = RecordingHandler.Client();
        handler.Response = _ => """{"status":"STATUS_FAILED"}""";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.MovePlaylistItemAsync("PLabc", "SET_A", "SET_B"));
    }

    [Fact]
    public async Task Move_SignedOut_ThrowsWithoutARequest()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);

        await Assert.ThrowsAsync<NotSignedInException>(
            () => client.MovePlaylistItemAsync("PLabc", "SET_A", "SET_B"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Insert_AddsThenMovesTheNewRowBefore()
    {
        var (client, handler) = RecordingHandler.Client();
        handler.Response = sent => sent.Body.GetProperty("actions")[0].GetProperty("action").GetString() == "ACTION_ADD_VIDEO"
            ? """{"status":"STATUS_SUCCEEDED","playlistEditResults":[{"playlistEditVideoAddedResultData":{"setVideoId":"NEW"}}]}"""
            : """{"status":"STATUS_SUCCEEDED"}""";

        Assert.Equal("NEW", await client.InsertIntoPlaylistAsync("PLabc", "vid", "SET_B"));

        Assert.Equal(2, handler.Requests.Count);
        var move = handler.Requests[1].Body.GetProperty("actions")[0];
        Assert.Equal("ACTION_MOVE_VIDEO_BEFORE", move.GetProperty("action").GetString());
        Assert.Equal("NEW", move.GetProperty("setVideoId").GetString());
        Assert.Equal("SET_B", move.GetProperty("movedSetVideoIdSuccessor").GetString());
    }
}
