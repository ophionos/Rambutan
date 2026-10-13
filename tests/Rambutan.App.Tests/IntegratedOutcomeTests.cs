using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>Toast outcomes for card play and the row "+" button.</summary>
public class IntegratedOutcomeTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly ToastHostViewModel _toasts = new(new ManualClock().Schedule);
    private readonly QueueModel _queue = new();
    private readonly TrackActionsViewModel _actions;

    public IntegratedOutcomeTests()
    {
        _actions = new TrackActionsViewModel(_catalog, _queue, null!, new TrackRatings(_catalog), _toasts);
    }

    [Fact]
    public async Task CardPlay_AFailedFetch_IsAnErrorToast()
    {
        var nav = new NavigationService(_catalog, null!, _actions);

        // FakeCatalog has no album set, so the fetch throws.
        await nav.PlayEntryAsync(new AlbumEntry { Title = "Discovery", BrowseId = "MPREb_x" });

        var toast = Assert.Single(_toasts.Toasts);
        Assert.True(toast.IsError);
        Assert.StartsWith("Couldn't play “Discovery”", toast.Message);
    }

    [Fact]
    public async Task CardPlay_AnEmptyAlbum_SaysSo()
    {
        _catalog.Album = new AlbumPage { BrowseId = "MPREb_e", Title = "Empty", Tracks = [] };
        var nav = new NavigationService(_catalog, null!, _actions);

        await nav.PlayEntryAsync(new AlbumEntry { Title = "Empty", BrowseId = "MPREb_e" });

        var toast = Assert.Single(_toasts.Toasts);
        Assert.False(toast.IsError);
        Assert.Equal("Nothing to play in “Empty”.", toast.Message);
    }

    [Fact]
    public void RowAddToQueue_UsesTheSharedAction_AndToasts()
    {
        var track = new TrackInfo { VideoId = "a", Title = "Aerodynamic", Artists = [] };

        _actions.AddToQueueCommand.Execute(track);

        Assert.Equal(["a"], _queue.Items.Select(t => t.VideoId));
        Assert.Equal("Added “Aerodynamic” to the queue", Assert.Single(_toasts.Toasts).Message);
    }
}
