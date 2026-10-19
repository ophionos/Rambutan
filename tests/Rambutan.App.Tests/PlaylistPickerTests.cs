using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>Add to playlist offers the playlists a song can be added to: the
/// user's own, from every page of the library.</summary>
public class PlaylistPickerTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly TrackActionsViewModel _actions;
    private IReadOnlyList<PlaylistEntry>? _offered;

    public PlaylistPickerTests()
    {
        _actions = new TrackActionsViewModel(
            _catalog, null!, null!, new TrackRatings(_catalog), new ToastHostViewModel(new ManualClock().Schedule))
        {
            PlaylistPicker = playlists =>
            {
                _offered = playlists;
                return Task.FromResult<PlaylistPickResult?>(null);
            },
        };
    }

    private static PlaylistEntry Playlist(string id, bool? isOwn) =>
        new() { Title = id, PlaylistId = id, IsOwn = isOwn };

    [Fact]
    public async Task OffersOwnPlaylists_NotSavedOnesOrLikedMusic()
    {
        var pick = _actions.AddToPlaylistCommand.ExecuteAsync(new TrackInfo { VideoId = "v", Title = "V", Artists = [] });

        _catalog.LibraryCalls[0].Result.SetResult(new LibraryPage
        {
            Items = [Playlist("LM", null), Playlist("PLown1", true), Playlist("PLsaved", false)],
            Continuation = new ContinuationToken("next"),
        });
        // The first page's result runs the command on to the next request.
        _catalog.LibraryCalls[1].Result.SetResult(new LibraryPage
        {
            Items = [Playlist("RDCLAKsaved", false), Playlist("PLown2", true), Playlist("PLunknown", null)],
        });
        await pick;

        // A card that doesn't say whose it is stays on offer.
        Assert.Equal(["PLown1", "PLown2", "PLunknown"], _offered!.Select(p => p.PlaylistId));
    }
}
