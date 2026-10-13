using Rambutan.Core.Catalog;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>Toast outcomes: playlist removal with Undo
/// and the shuffle mode's report.</summary>
public class OutcomeToastTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly ManualClock _clock = new();
    private readonly ToastHostViewModel _toasts;
    private readonly PlaylistPageViewModel _page;

    public OutcomeToastTests()
    {
        _toasts = new ToastHostViewModel(_clock.Schedule);
        var actions = new TrackActionsViewModel(_catalog, null!, null!, new TrackRatings(_catalog), _toasts);
        _page = new PlaylistPageViewModel(_catalog, null!, null!, actions, "PLmine") { IsEditable = true };
        foreach (var id in new[] { "a", "b", "c", "d" })
            _page.Tracks.Add(Row(id));
    }

    private static TrackInfo Row(string id) =>
        new() { VideoId = id, Title = id.ToUpperInvariant(), Artists = [], SetVideoId = $"set-{id}" };

    private string[] Ids() => _page.Tracks.Select(t => t.VideoId).ToArray();

    private async Task<Toast> RemoveAsync(string id)
    {
        await _page.RemoveTrackCommand.ExecuteAsync(_page.Tracks.Single(t => t.VideoId == id));
        return _toasts.Toasts[^1];
    }

    [Fact]
    public async Task Remove_OffersUndo_WhichPutsTheRowBackWhereItWas()
    {
        var toast = await RemoveAsync("b");

        Assert.Equal(["a", "c", "d"], Ids());
        Assert.Equal("Removed “B”", toast.Message);
        Assert.Equal("Undo", toast.ActionLabel);

        await toast.ActCommand.ExecuteAsync(null);

        Assert.Equal(["a", "b", "c", "d"], Ids());
        Assert.Equal(["remove set-b", "insert b before set-c"], _catalog.PlaylistEdits);
        // The restored row carries its new server id, so it can be removed again.
        Assert.Equal("new-b", _page.Tracks[1].SetVideoId);
        Assert.Equal("Restored “B”", Assert.Single(_toasts.Toasts).Message);
    }

    [Fact]
    public async Task Undo_OfTheLastRow_AppendsItAtTheEnd()
    {
        var toast = await RemoveAsync("d");
        await toast.ActCommand.ExecuteAsync(null);

        Assert.Equal(["a", "b", "c", "d"], Ids());
        Assert.Equal("insert d before end", _catalog.PlaylistEdits[^1]);
    }

    [Fact]
    public async Task Undo_AfterAnotherRemoval_LandsBeforeWhateverNowHoldsItsPlace()
    {
        var first = await RemoveAsync("b");
        await RemoveAsync("c");

        await first.ActCommand.ExecuteAsync(null);

        Assert.Equal(["a", "b", "d"], Ids());
        Assert.Equal("insert b before set-d", _catalog.PlaylistEdits[^1]);
    }

    [Fact]
    public async Task Undo_WithoutANewRowId_SaysWhereTheTrackWent()
    {
        _catalog.NewSetVideoId = _ => null;
        var toast = await RemoveAsync("a");

        await toast.ActCommand.ExecuteAsync(null);

        Assert.Equal(["b", "c", "d", "a"], Ids());
        Assert.Equal("Restored “A” at the end of the playlist", _toasts.Toasts[^1].Message);
    }

    [Fact]
    public async Task FailedUndo_IsAnErrorToast_AndLeavesTheListAlone()
    {
        var toast = await RemoveAsync("b");
        _catalog.EditFailure = new HttpRequestException("offline");

        await toast.ActCommand.ExecuteAsync(null);

        Assert.Equal(["a", "c", "d"], Ids());
        var error = Assert.Single(_toasts.Toasts);
        Assert.True(error.IsError);
        Assert.Equal("Couldn't restore “B”: offline", error.Message);
    }

    [Fact]
    public async Task FailedRemove_IsAnErrorToast_WithNoUndo()
    {
        _catalog.EditFailure = new HttpRequestException("offline");

        var toast = await RemoveAsync("b");

        Assert.Equal(["a", "b", "c", "d"], Ids());
        Assert.True(toast.IsError);
        Assert.False(toast.HasAction);
    }

    [Fact]
    public void ShuffleToggle_IsAMode_AndReportsWhatItDid()
    {
        Headless.Run(() =>
        {
            var queue = new QueueModel(new Random(1));
            queue.SetQueue(new[] { "a", "b", "c", "d", "e" }.Select(id => new TrackInfo { VideoId = id, Title = id, Artists = [] }));
            var player = new PlayerBarViewModel(null!, queue, null, _toasts);

            player.ToggleShuffleCommand.Execute(null);
            Assert.True(player.IsShuffleOn);
            Assert.True(queue.Shuffle);
            Assert.Equal("Shuffle: on", player.ShuffleTip);
            Assert.Equal("Shuffle on — 4 upcoming tracks shuffled", _toasts.Toasts[^1].Message);

            player.ToggleShuffleCommand.Execute(null);
            Assert.False(player.IsShuffleOn);
            Assert.Equal(["a", "b", "c", "d", "e"], queue.Items.Select(t => t.VideoId));
            Assert.Equal("Shuffle off — original order restored", _toasts.Toasts[^1].Message);
        });
    }
}
