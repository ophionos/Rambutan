using Rambutan.App.ViewModels;
using Rambutan.Core.Downloads;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>An album's or playlist's Download button shows the collection's state,
/// and once all is downloaded removes the downloads, with Undo that works offline
/// because nothing is deleted until the toast goes.</summary>
public sealed class CollectionDownloadButtonTests
{
    private readonly ManualClock _clock = new();
    private readonly ToastHostViewModel _toasts;
    private readonly DoneStore _store = new();
    private readonly TrackActionsViewModel _actions;
    private readonly FakeCatalog _catalog = new();

    public CollectionDownloadButtonTests()
    {
        _toasts = new ToastHostViewModel(_clock.Schedule);
        _actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, _toasts)
        {
            Downloads = new DownloadQueue(_store),
            RunInBackground = run =>
            {
                run();
                return Task.CompletedTask;
            },
        };
    }

    /// <summary>Finished downloads only; a queued job never finishes.</summary>
    private sealed class DoneStore : IDownloadStore
    {
        public readonly HashSet<string> Done = [];
        public readonly List<string> Deleted = [];
        public IReadOnlyList<DownloadedTrack> List() =>
            Done.Select(id => new DownloadedTrack(Track(id), 1, DateTimeOffset.UnixEpoch)).ToList();
        public bool Contains(string videoId) => Done.Contains(videoId);
        public Task DownloadAsync(TrackInfo track, DownloadOrder order, IProgress<DownloadProgress> progress, CancellationToken ct) =>
            Task.Delay(Timeout.Infinite, ct);
        public void Delete(string videoId)
        {
            Done.Remove(videoId);
            Deleted.Add(videoId);
        }
        public IReadOnlyList<PendingDownload> LoadPending() => [];
        public void SavePending(IReadOnlyList<PendingDownload> pending) { }
    }

    private static TrackInfo Track(string id) => new() { VideoId = id, Title = "Song " + id, Artists = [] };

    private async Task<AlbumPageViewModel> AlbumAsync(params string[] downloaded)
    {
        _store.Done.UnionWith(downloaded);
        _actions.Downloads = _actions.Downloads; // re-reads the store
        _catalog.Album = new AlbumPage { BrowseId = "MPREb_1", Title = "25", Tracks = [Track("t1"), Track("t2")] };
        var page = new AlbumPageViewModel(_catalog, null!, "MPREb_1") { Actions = _actions };
        await page.LoadAsync();
        return page;
    }

    private async Task<AlbumPageViewModel> MusicVideoAlbumAsync(params string[] downloaded)
    {
        _store.Done.UnionWith(downloaded);
        _actions.Downloads = _actions.Downloads; // re-reads the store
        _catalog.Album = new AlbumPage
        {
            BrowseId = "MPREb_1", Title = "RAM",
            Tracks = [Track("omv1") with { SongId = "atv1" }, Track("omv2") with { SongId = "atv2" }],
        };
        var page = new AlbumPageViewModel(_catalog, null!, "MPREb_1") { Actions = _actions };
        await page.LoadAsync();
        return page;
    }

    [Fact]
    public async Task AlbumRows_PlayAndDownloadTheAudioTrack()
    {
        var page = await MusicVideoAlbumAsync();

        Assert.Equal(["atv1", "atv2"], page.Tracks.Select(t => t.VideoId));
        Assert.Equal(["omv1", "omv2"], page.Tracks.Select(t => t.ListedVideoId));
        await page.DownloadButton!.PressCommand.ExecuteAsync(null);
        Assert.Equal(["atv1", "atv2"], _actions.Downloads!.Jobs.Select(j => j.Track.VideoId));
    }

    [Fact]
    public async Task AMusicVideoDownloadedEarlier_StaysWhatTheRowPlays()
    {
        // Existing downloads keep the music video and aren't fetched again.
        var page = await MusicVideoAlbumAsync("omv1");

        Assert.Equal(["omv1", "atv2"], page.Tracks.Select(t => t.VideoId));
        Assert.Equal(1, page.DownloadButton!.State.Downloaded);
        await page.DownloadButton.PressCommand.ExecuteAsync(null);
        Assert.Equal(["atv2"], _actions.Downloads!.Jobs.Select(j => j.Track.VideoId));
    }

    [Fact]
    public async Task NothingDownloaded_OffersDownload()
    {
        var button = (await AlbumAsync()).DownloadButton!;
        Assert.Equal(CollectionDownloadKind.NotDownloaded, button.State.Kind);
        Assert.Equal("Download", button.Name);
    }

    [Fact]
    public async Task APress_StartsDownloading_WithItsProgress()
    {
        var button = (await AlbumAsync("t1")).DownloadButton!;
        await button.PressCommand.ExecuteAsync(null);
        button.Refresh();

        Assert.True(button.IsDownloading);
        Assert.Equal("Downloading, 1 of 2", button.Name);
        Assert.Equal(180, button.SweepAngle);
    }

    [Fact]
    public async Task AllDownloaded_ThePressRemoves_ButDeletesOnlyWhenTheToastGoes()
    {
        var button = (await AlbumAsync("t1", "t2")).DownloadButton!;
        Assert.True(button.IsDownloaded);
        Assert.Equal("Remove downloads", button.Name);

        await button.PressCommand.ExecuteAsync(null);

        Assert.Equal(CollectionDownloadKind.NotDownloaded, button.State.Kind);
        Assert.Empty(_actions.DownloadedIds);
        Assert.Empty(_store.Deleted);
        Assert.Equal("Removed 2 downloads from 25", Assert.Single(_toasts.Toasts).Message);

        _clock.Advance(ToastHostViewModel.LongLifetime);

        Assert.Equal(["t1", "t2"], _store.Deleted.Order());
    }

    [Fact]
    public async Task Undo_DeletesNothing_AndBringsTheDownloadsBack()
    {
        var button = (await AlbumAsync("t1", "t2")).DownloadButton!;
        await button.PressCommand.ExecuteAsync(null);

        await Assert.Single(_toasts.Toasts).ActCommand.ExecuteAsync(null);
        _clock.Advance(ToastHostViewModel.LongLifetime);

        Assert.Empty(_store.Deleted);
        Assert.True(button.IsDownloaded);
    }

    [Fact]
    public async Task DownloadAllWhileUndoIsUp_TakesTheDownloadsBack()
    {
        var page = await AlbumAsync("t1", "t2");
        await page.DownloadButton!.PressCommand.ExecuteAsync(null);

        await page.DownloadButton.PressCommand.ExecuteAsync(null); // now reads "Download"
        Assert.Contains(_toasts.Toasts, t => t.Message == "Kept 2 downloads");
        _clock.Advance(ToastHostViewModel.LongLifetime);

        Assert.Empty(_store.Deleted);
        Assert.True(page.DownloadButton.IsDownloaded);
    }

    [Fact]
    public async Task Exit_DeletesWhatIsStillWaitingOnItsToast()
    {
        var button = (await AlbumAsync("t1", "t2")).DownloadButton!;
        await button.PressCommand.ExecuteAsync(null);

        _actions.FlushPendingRemovals();

        Assert.Equal(2, _store.Deleted.Count);
    }

    [Fact]
    public async Task APlaylistNotScrolledToItsEnd_DoesntClaimAllDownloaded()
    {
        _store.Done.Add("t1");
        _actions.Downloads = _actions.Downloads;
        _catalog.Playlist = new PlaylistPage
        {
            PlaylistId = "PL1", Title = "Road trip", Tracks = [Track("t1")], Continuation = new ContinuationToken("p2"),
        };
        var page = new PlaylistPageViewModel(_catalog, null!, null!, _actions, "PL1");
        await page.LoadAsync();

        Assert.Equal(CollectionDownloadKind.NotDownloaded, page.DownloadButton!.State.Kind);
    }

    [Fact]
    public async Task TheDownloadsPage_HidesThemWhileUndoIsUp()
    {
        var button = (await AlbumAsync("t1", "t2")).DownloadButton!;
        var downloads = new DownloadsPageViewModel(_actions.Downloads!, null, _actions, post: a => a());
        Assert.Equal(2, downloads.Downloaded.Count);

        await button.PressCommand.ExecuteAsync(null);
        Assert.Empty(downloads.Downloaded);

        await Assert.Single(_toasts.Toasts).ActCommand.ExecuteAsync(null);
        Assert.Equal(2, downloads.Downloaded.Count);
    }

    [Fact]
    public async Task WhileTheFilesAreDeleted_TheTracksStayHidden()
    {
        var deleting = new TaskCompletionSource();
        var actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, _toasts)
        {
            Downloads = new DownloadQueue(_store),
            RunInBackground = run => deleting.Task.ContinueWith(_ => run()),
        };
        _store.Done.UnionWith(["t1", "t2"]);
        actions.Downloads = actions.Downloads;
        actions.RemoveDownloads(["t1", "t2"], "25");

        _clock.Advance(ToastHostViewModel.LongLifetime);
        Assert.Empty(_store.Deleted);
        Assert.True(actions.IsPendingRemoval("t1"));
        Assert.Empty(actions.DownloadedIds);

        deleting.SetResult();
        for (var i = 0; i < 200 && _store.Deleted.Count < 2; i++)
            await Task.Delay(10);
        Assert.Equal(2, _store.Deleted.Count);
    }
}
