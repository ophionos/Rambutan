using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.Core.Tests;

public class SongLibraryTests
{
    private static readonly LibraryToggle Saved = new(true, "ADD", "REMOVE");
    private static readonly LibraryToggle NotSaved = new(false, "ADD", "REMOVE");

    private static TrackInfo Track(LibraryToggle? library, string id = "v1") =>
        new() { VideoId = id, Title = id, Artists = [], Library = library };

    [Fact]
    public void Of_FallsBackToTheSourceSnapshot()
    {
        var library = new SongLibrary(new TrackRatingsTests.RatingCatalog());

        Assert.True(library.Of(Track(Saved)));
        Assert.False(library.Of(Track(NotSaved)));
        Assert.Null(library.Of(Track(null)));
    }

    [Fact]
    public async Task Toggle_FlipsTheSnapshotAndEveryRecordAgrees()
    {
        var catalog = new TrackRatingsTests.RatingCatalog();
        var library = new SongLibrary(catalog);

        Assert.False(await library.ToggleAsync(Track(Saved)));

        Assert.Equal([(Saved, false)], catalog.SongEdits);
        // Another record of the same song, from another page, agrees.
        Assert.False(library.Of(Track(Saved)));

        Assert.True(await library.ToggleAsync(Track(Saved)));
        Assert.Equal((Saved, true), catalog.SongEdits[1]);
    }

    [Fact]
    public async Task Toggle_AppliesBeforeTheRequestCompletes()
    {
        var catalog = new TrackRatingsTests.RatingCatalog { Gate = new TaskCompletionSource() };
        var library = new SongLibrary(catalog);
        var changed = new List<string>();
        library.Changed += changed.Add;

        var pending = library.ToggleAsync(Track(NotSaved));

        Assert.False(pending.IsCompleted);
        Assert.True(library.Of(Track(NotSaved)));
        Assert.Equal(["v1"], changed);

        catalog.Gate.SetResult();
        Assert.True(await pending);
    }

    [Fact]
    public async Task Toggle_Failure_RevertsAndRethrows()
    {
        var catalog = new TrackRatingsTests.RatingCatalog { Failure = new HttpRequestException("offline") };
        var library = new SongLibrary(catalog);
        var changed = new List<string>();
        library.Changed += changed.Add;

        await Assert.ThrowsAsync<HttpRequestException>(() => library.ToggleAsync(Track(NotSaved)));

        Assert.False(library.Of(Track(NotSaved)));
        Assert.Equal(["v1", "v1"], changed); // applied, then undone
    }

    [Fact]
    public async Task Toggle_FailureAfterANewerPress_LeavesTheNewerPress()
    {
        var catalog = new TrackRatingsTests.RatingCatalog { Gate = new TaskCompletionSource() };
        var library = new SongLibrary(catalog);

        var first = library.ToggleAsync(Track(NotSaved));     // → saved, held
        var held = catalog.Gate;
        catalog.Gate = null;
        await library.ToggleAsync(Track(NotSaved));           // → not saved, sent

        catalog.Failure = new HttpRequestException("late failure");
        held.SetResult();
        await Assert.ThrowsAsync<HttpRequestException>(() => first);

        Assert.False(library.Of(Track(NotSaved)));
    }

    [Fact]
    public async Task Toggle_WithoutTokens_ThrowsWithoutARequest()
    {
        var catalog = new TrackRatingsTests.RatingCatalog();
        var library = new SongLibrary(catalog);

        await Assert.ThrowsAsync<InvalidOperationException>(() => library.ToggleAsync(Track(null)));

        Assert.Empty(catalog.SongEdits);
    }
}
