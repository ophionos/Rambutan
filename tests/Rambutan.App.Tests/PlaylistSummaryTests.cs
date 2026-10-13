using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>
/// The playlist header's "N songs • length" follows removals and undos:
/// recounted from the rows when every page is loaded, read again otherwise.
/// </summary>
public class PlaylistSummaryTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly ToastHostViewModel _toasts = new(new ManualClock().Schedule);
    private readonly PlaylistPageViewModel _page;

    public PlaylistSummaryTests()
    {
        var actions = new TrackActionsViewModel(_catalog, null!, null!, new TrackRatings(_catalog), _toasts);
        _page = new PlaylistPageViewModel(_catalog, null!, null!, actions, "PL1");
    }

    private static TrackInfo Row(string id) => new()
    {
        VideoId = id, Title = id.ToUpperInvariant(), Artists = [], SetVideoId = $"set-{id}",
        Duration = TimeSpan.FromMinutes(3),
    };

    private static PlaylistPage Page(string summary, string? next, params string[] ids) => new()
    {
        PlaylistId = "PL1", Title = "Mine", SecondSubtitle = summary, IsEditable = true,
        Tracks = ids.Select(Row).ToArray(),
        Continuation = next is null ? null : new ContinuationToken(next),
    };

    [Fact]
    public async Task WithEveryPageLoaded_RemoveAndUndo_RecountTheRows()
    {
        _catalog.Playlist = Page("3 songs • 9 minutes", null, "a", "b", "c");
        await _page.LoadAsync();
        Assert.Equal("3 songs • 9 minutes", _page.Summary);

        await _page.RemoveTrackCommand.ExecuteAsync(_page.Tracks[1]);
        Assert.Equal("2 songs • 6 min", _page.Summary);

        await _toasts.Toasts[^1].ActCommand.ExecuteAsync(null);
        Assert.Equal("3 songs • 9 min", _page.Summary);
    }

    [Fact]
    public async Task WithPagesStillToLoad_RemoveReadsTheHeaderAgain()
    {
        _catalog.Playlist = Page("100 songs • 5+ hours", "more", "a", "b", "c");
        await _page.LoadAsync();

        _catalog.Playlist = Page("99 songs • 5+ hours", "more", "a", "c");
        await _page.RemoveTrackCommand.ExecuteAsync(_page.Tracks[1]);

        Assert.Equal("99 songs • 5+ hours", _page.Summary);
    }

    [Fact]
    public async Task WhenTheHeaderCantBeReadAgain_TheOldTextStays()
    {
        _catalog.Playlist = Page("100 songs • 5+ hours", "more", "a", "b", "c");
        await _page.LoadAsync();

        _catalog.Playlist = Page("99 songs • 5+ hours", "more", "a", "c") with { KeptAt = DateTimeOffset.Now };
        await _page.RemoveTrackCommand.ExecuteAsync(_page.Tracks[1]);

        Assert.Equal("100 songs • 5+ hours", _page.Summary);
    }
}
