using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>Drag-reorder on a playlist page: only on playlists the account owns,
/// the row moves at once, and a refused move puts it back.</summary>
public class PlaylistReorderTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly ToastHostViewModel _toasts = new(new ManualClock().Schedule);
    private readonly PlaylistPageViewModel _page;

    public PlaylistReorderTests()
    {
        var actions = new TrackActionsViewModel(_catalog, null!, null!, new TrackRatings(_catalog), _toasts);
        _page = new PlaylistPageViewModel(_catalog, null!, null!, actions, "PLmine");
    }

    private static TrackInfo Row(string id) =>
        new() { VideoId = id, Title = id.ToUpperInvariant(), Artists = [], SetVideoId = $"set-{id}" };

    private static PlaylistPage Page(bool editable, string? next, params string[] ids) => new()
    {
        PlaylistId = "PLmine",
        Title = "Mine",
        Tracks = ids.Select(Row).ToArray(),
        Continuation = next is null ? null : new ContinuationToken(next),
        IsEditable = editable,
    };

    private async Task LoadAsync(bool editable = true, string? next = null)
    {
        _catalog.Playlist = Page(editable, next, "a", "b", "c", "d");
        await _page.LoadAsync();
    }

    private string[] Ids() => _page.Tracks.Select(t => t.VideoId).ToArray();

    private Task MoveAsync(int from, int to) =>
        _page.DragReorderCommand!.ExecuteAsync(new ReorderRequest(from, to));

    [Fact]
    public async Task MovingDown_PlacesTheRowBeforeTheOneAfterIt()
    {
        await LoadAsync();

        await MoveAsync(0, 2);

        Assert.Equal(["b", "c", "a", "d"], Ids());
        Assert.Equal(["move set-a before set-d"], _catalog.PlaylistEdits);
    }

    [Fact]
    public async Task MovingUp_PlacesTheRowBeforeTheOneItLandsOn()
    {
        await LoadAsync();

        await MoveAsync(3, 1);

        Assert.Equal(["a", "d", "b", "c"], Ids());
        Assert.Equal(["move set-d before set-b"], _catalog.PlaylistEdits);
    }

    [Fact]
    public async Task MovingLast_OnAFullyLoadedPlaylist_MovesToTheEnd()
    {
        await LoadAsync();

        await MoveAsync(1, 3);

        Assert.Equal(["a", "c", "d", "b"], Ids());
        Assert.Equal(["move set-b before end"], _catalog.PlaylistEdits);
    }

    [Fact]
    public async Task MovingLast_WithPagesNotLoaded_LoadsTheNextPageForTheRowAfterIt()
    {
        await LoadAsync(next: "page2");
        _catalog.PlaylistMore = _ => Task.FromResult(Page(true, null, "e", "f"));

        await MoveAsync(1, 3);

        Assert.Equal(["a", "c", "d", "b", "e", "f"], Ids());
        Assert.Equal(["move set-b before set-e"], _catalog.PlaylistEdits);
    }

    [Fact]
    public async Task MovingLast_WhenTheNextPageFails_PutsTheRowBackAndSendsNothing()
    {
        await LoadAsync(next: "page2");
        _catalog.PlaylistMore = _ => Task.FromException<PlaylistPage>(new HttpRequestException("offline"));

        await MoveAsync(1, 3);

        Assert.Equal(["a", "b", "c", "d"], Ids());
        Assert.Empty(_catalog.PlaylistEdits);
        Assert.True(_toasts.Toasts[^1].IsError);
        Assert.StartsWith("Couldn't move “B”", _toasts.Toasts[^1].Message);
    }

    [Fact]
    public async Task MovingLast_WhileTheNextPageIsStillLoading_PutsTheRowBackAndSendsNothing()
    {
        await LoadAsync(next: "page2");
        var page2 = new TaskCompletionSource<PlaylistPage>();
        _catalog.PlaylistMore = _ => page2.Task;
        var loading = _page.LoadMoreCommand.ExecuteAsync(null);

        await MoveAsync(1, 3);

        Assert.Equal(["a", "b", "c", "d"], Ids());
        Assert.Empty(_catalog.PlaylistEdits);
        Assert.Equal("Couldn't move “B”: the rest of the playlist hasn't loaded yet", _toasts.Toasts[^1].Message);

        page2.SetResult(Page(true, null, "e"));
        await loading;
    }

    [Fact]
    public async Task MovingLast_WhenTheNextPageHasNoRows_PutsTheRowBack()
    {
        await LoadAsync(next: "page2");
        _catalog.PlaylistMore = _ => Task.FromResult(Page(true, "page3"));

        await MoveAsync(1, 3);

        Assert.Equal(["a", "b", "c", "d"], Ids());
        Assert.Empty(_catalog.PlaylistEdits);
    }

    [Fact]
    public async Task ARefusedMove_PutsTheRowBack_AndSaysWhy()
    {
        await LoadAsync();
        _catalog.EditFailure = new HttpRequestException("offline");

        await MoveAsync(0, 2);

        Assert.Equal(["a", "b", "c", "d"], Ids());
        var error = Assert.Single(_toasts.Toasts);
        Assert.True(error.IsError);
        Assert.Equal("Couldn't move “A”: offline", error.Message);
    }

    [Fact]
    public async Task ASignedOutMove_AsksToSignIn()
    {
        await LoadAsync();
        _catalog.EditFailure = new NotSignedInException();

        await MoveAsync(0, 2);

        Assert.Equal(["a", "b", "c", "d"], Ids());
        Assert.Equal("Sign in to edit playlists.", Assert.Single(_toasts.Toasts).Message);
    }

    [Fact]
    public async Task APlaylistTheAccountDoesNotOwn_CannotBeReordered()
    {
        await LoadAsync(editable: false);

        Assert.Null(_page.DragReorderCommand);
        Assert.Null(_page.ListHelp);
        Assert.False(_page.ReorderCommand.CanExecute(new ReorderRequest(0, 2)));
    }

    [Fact]
    public async Task AnOwnedPlaylist_NamesTheMoveKeysToScreenReaders()
    {
        await LoadAsync();

        Assert.NotNull(_page.DragReorderCommand);
        Assert.Contains("Alt+Up", _page.ListHelp);
    }

    [Fact]
    public async Task ARowWithoutARowId_BlocksReordering()
    {
        await LoadAsync();
        _page.Tracks[2] = _page.Tracks[2] with { SetVideoId = null };

        Assert.False(_page.ReorderCommand.CanExecute(new ReorderRequest(0, 1)));
    }
}
