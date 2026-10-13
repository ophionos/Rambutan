using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>Rename, Delete and Remove from playlist exist only on a playlist the
/// account owns: an editorial or someone else's playlist (or any playlist while
/// signed out) offers none of them.</summary>
public class PlaylistOwnershipTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly TrackActionsViewModel _actions;
    private readonly PlaylistPageViewModel _page;

    public PlaylistOwnershipTests()
    {
        _actions = new TrackActionsViewModel(
            _catalog, null!, null!, new TrackRatings(_catalog), new ToastHostViewModel(new ManualClock().Schedule));
        _page = new PlaylistPageViewModel(_catalog, null!, null!, _actions, "PL1");
    }

    private async Task LoadAsync(bool editable)
    {
        _catalog.Playlist = new PlaylistPage
        {
            PlaylistId = "PL1",
            Title = "'80s Pop",
            Tracks = [new TrackInfo { VideoId = "a", Title = "A", Artists = [], SetVideoId = "set-a" }],
            IsEditable = editable,
        };
        await _page.LoadAsync();
    }

    [Fact]
    public async Task APlaylistTheAccountDoesNotOwn_OffersNoEdits()
    {
        await LoadAsync(editable: false);
        var row = _page.Tracks[0];

        Assert.False(_page.RenameCommand.CanExecute(null));
        Assert.False(_page.DeleteCommand.CanExecute(null));
        Assert.False(_page.RemoveTrackCommand.CanExecute(row));
        Assert.Equal(TrackMenuExtras.None, _page.MenuExtrasFor(null, row));
    }

    [Fact]
    public async Task RemovingFromAPlaylistTheAccountDoesNotOwn_SendsNothing()
    {
        await LoadAsync(editable: false);

        await _page.RemoveTrackCommand.ExecuteAsync(_page.Tracks[0]);

        Assert.Empty(_catalog.PlaylistEdits);
        Assert.Single(_page.Tracks);
    }

    [Fact]
    public async Task AnOwnedPlaylist_OffersRenameDeleteAndRemove()
    {
        await LoadAsync(editable: true);
        var row = _page.Tracks[0];

        Assert.True(_page.RenameCommand.CanExecute(null));
        Assert.True(_page.DeleteCommand.CanExecute(null));
        Assert.True(_page.RemoveTrackCommand.CanExecute(row));
        Assert.NotEqual(TrackMenuExtras.None, _page.MenuExtrasFor(null, row));
    }
}
