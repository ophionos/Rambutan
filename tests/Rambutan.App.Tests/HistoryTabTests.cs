using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>
/// Library's History tab: YouTube Music's rows under its own headings, played
/// with the whole history as the queue, and removed one at a time from the row's
/// menu, with no Undo, since YouTube Music can't put a row back.
/// </summary>
public sealed class HistoryTabTests : IDisposable
{
    private readonly FakeCatalog _catalog = new();
    private readonly ToastHostViewModel _toasts = new(new ManualClock().Schedule);
    private readonly QueueModel _queue = new();
    private readonly PlaybackCoordinator _coordinator;
    private readonly TrackActionsViewModel _actions;

    public HistoryTabTests()
    {
        _coordinator = FakePlayback.Coordinator(_queue);
        _actions = new TrackActionsViewModel(null!, _queue, null!, null!, _toasts);
    }

    public void Dispose() => _coordinator.Dispose();

    private static HistoryEntry Entry(string id, string played, string? token = "T_") => new()
    {
        Title = "Title " + id,
        Track = new TrackInfo { VideoId = id, Title = "Title " + id, Artists = [] },
        Played = played,
        RemoveToken = token is null ? null : token + id,
    };

    private readonly HistoryEntry[] _history =
    [
        Entry("a", "Today"), Entry("b", "Today"), Entry("c", "Yesterday"), Entry("d", "October 2026", token: null),
    ];

    private async Task<LibraryPageViewModel> ShowHistoryAsync()
    {
        var library = new LibraryPageViewModel(_catalog, null!, _actions, _coordinator);
        var shown = library.EnsureLoadedAsync();
        _catalog.LibraryCalls.Single().Complete(null, "Mix");
        await shown;
        library.SelectedSection = library.History;
        _catalog.LibraryCalls.Single(c => c.What == "history").Complete(null, _history);
        return library;
    }

    [Fact]
    public async Task The_rows_are_grouped_under_YouTube_Musics_headings_in_order()
    {
        var library = await ShowHistoryAsync();

        Assert.True(library.SelectedSection.IsHistory);
        Assert.True(library.SelectedSection.ShowsRows);
        Assert.False(library.SelectedSection.IsSongs);
        Assert.Equal(
            [("Today", ["a", "b"]), ("Yesterday", ["c"]), ("October 2026", ["d"])],
            library.History.Groups.Select(g => (g.Played, g.Entries.Select(e => e.Track.VideoId).ToArray())));
    }

    [Fact]
    public async Task A_heading_that_comes_back_later_starts_a_group_of_its_own()
    {
        var library = new LibraryPageViewModel(_catalog, null!, _actions, _coordinator);
        library.SelectedSection = library.History;
        var shown = library.EnsureLoadedAsync();
        _catalog.LibraryCalls.Single().Complete(null, [Entry("a", "Today"), Entry("b", "Yesterday"), Entry("c", "Today")]);
        await shown;

        Assert.Equal(["Today", "Yesterday", "Today"], library.History.Groups.Select(g => g.Played));
    }

    [Fact]
    public async Task A_song_plays_with_the_whole_history_as_its_queue()
    {
        var library = await ShowHistoryAsync();

        await library.PlayHistoryEntryCommand.ExecuteAsync(_history[2]);

        Assert.Equal(["a", "b", "c", "d"], _queue.Items.Select(t => t.VideoId));
        Assert.Equal("c", _queue.Current?.VideoId);
    }

    [Fact]
    public async Task The_menu_offers_removal_only_for_a_row_that_has_a_token()
    {
        var library = await ShowHistoryAsync();

        var removable = library.MenuExtrasFor(_history[0], _history[0].Track);
        var entry = Assert.Single(removable.Edit!);
        Assert.Equal("Remove from history", entry.Header);
        Assert.Same(_history[0], entry.Parameter);

        Assert.Same(TrackMenuExtras.None, library.MenuExtrasFor(_history[3], _history[3].Track));
        // A row of another tab is a track, not a history entry.
        Assert.Same(TrackMenuExtras.None, library.MenuExtrasFor(_history[0].Track, _history[0].Track));
    }

    [Fact]
    public async Task Removing_sends_the_rows_token_and_drops_the_row_and_its_empty_heading()
    {
        var library = await ShowHistoryAsync();

        await library.RemoveFromHistoryCommand.ExecuteAsync(_history[2]);

        Assert.Equal(["forget T_c"], _catalog.LibraryEdits);
        Assert.Equal(["a", "b", "d"], library.History.Items.Select(e => ((HistoryEntry)e).Track.VideoId));
        Assert.Equal(["Today", "October 2026"], library.History.Groups.Select(g => g.Played));
        var toast = Assert.Single(_toasts.Toasts);
        Assert.False(toast.IsError);
        Assert.Equal("Removed “Title c” from your history", toast.Message);
        Assert.Null(toast.ActionLabel);
    }

    [Fact]
    public async Task A_refused_removal_keeps_the_row_and_says_so()
    {
        var library = await ShowHistoryAsync();
        _catalog.EditFailure = new HttpRequestException("offline");

        await library.RemoveFromHistoryCommand.ExecuteAsync(_history[0]);

        Assert.Equal(4, library.History.Items.Count);
        var toast = Assert.Single(_toasts.Toasts);
        Assert.True(toast.IsError);
        Assert.Equal("Couldn't remove “Title a” from your history: offline", toast.Message);
    }

    [Fact]
    public async Task Signed_out_the_removal_asks_to_sign_in()
    {
        var library = await ShowHistoryAsync();
        _catalog.EditFailure = new NotSignedInException();

        await library.RemoveFromHistoryCommand.ExecuteAsync(_history[0]);

        Assert.True(library.History.NeedsSignIn);
        Assert.Empty(_toasts.Toasts);
    }
}
