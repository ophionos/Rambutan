using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Downloads;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>The kept copies of downloaded albums and playlists, as the pages and
/// the Library tabs show them.</summary>
public sealed class OfflinePagesTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly MemoryStore _kept = new();
    private readonly TrackActionsViewModel _actions;

    public OfflinePagesTests()
    {
        _actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, new ToastHostViewModel())
        {
            Downloads = new DownloadQueue(new PendingStore()),
            KeptCollections = new OfflineCollections(_kept, _ => true, _ => false),
        };
    }

    private sealed class MemoryStore : IKeptCollectionStore
    {
        public List<KeptCollection> Saved { get; private set; } = [];

        public IReadOnlyList<KeptCollection> Load() => Saved;

        public void Save(IReadOnlyList<KeptCollection> collections) => Saved = [.. collections];
    }

    /// <summary>Downloads that never finish: enough to queue against.</summary>
    private sealed class PendingStore : IDownloadStore
    {
        public IReadOnlyList<DownloadedTrack> List() => [];
        public bool Contains(string videoId) => false;
        public Task DownloadAsync(TrackInfo track, DownloadOrder order, IProgress<DownloadProgress> progress, CancellationToken ct) =>
            Task.Delay(Timeout.Infinite, ct);
        public void Delete(string videoId) { }
        public IReadOnlyList<PendingDownload> LoadPending() => [];
        public void SavePending(IReadOnlyList<PendingDownload> pending) { }
    }

    private static TrackInfo Track(string id) => new() { VideoId = id, Title = "Song " + id, Artists = [] };

    private static readonly DateTimeOffset KeptAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static AlbumPage Album(DateTimeOffset? keptAt = null) => new()
    {
        BrowseId = "MPREb_25", Title = "25", Artist = "Adele", ArtistBrowseId = "UCadele",
        SubtitleInfo = "Album • 2015", ThumbnailUrl = "https://lh3/25", AudioPlaylistId = "OLAK5uy_25",
        InLibrary = keptAt is null ? false : null,
        Tracks = [Track("t1"), Track("t2")],
        KeptAt = keptAt,
    };

    private static PlaylistPage Playlist(DateTimeOffset? keptAt = null) => new()
    {
        PlaylistId = "PL1", Title = "Road trip", SubtitleInfo = "Playlist • 2026", ThumbnailUrl = "https://lh3/pl",
        Author = new ArtistRef("Mira", "UCmira"),
        IsEditable = keptAt is null, Tracks = [Track("t1")], KeptAt = keptAt,
    };

    [Fact]
    public async Task ALiveAlbum_IsKeptByDownloadAll_WithItsHeader()
    {
        _catalog.Album = Album();
        var page = new AlbumPageViewModel(_catalog, null!, "MPREb_25") { Actions = _actions };
        await page.LoadAsync();

        page.DownloadAllCommand.Execute(null);

        var copy = Assert.Single(_kept.Saved);
        Assert.Equal(
            (KeptCollectionKind.Album, "MPREb_25", "25", "Adele", "UCadele", "Album • 2015", "https://lh3/25"),
            (copy.Kind, copy.Id, copy.Title, copy.Artist, copy.ArtistBrowseId, copy.SubtitleInfo, copy.ThumbnailUrl));
        // The rows as queued: with the album's name and art filled in.
        Assert.All(copy.Tracks, t => Assert.Equal(("25", "MPREb_25"), (t.Album, t.AlbumBrowseId)));
        Assert.Null(page.KeptNotice);
    }

    [Fact]
    public async Task AnAlbumCopy_SaysSo_OffersNoSave_IsntKeptAgain_AndRetryLoadsOverIt()
    {
        _catalog.Album = Album(KeptAt);
        var page = new AlbumPageViewModel(_catalog, null!, "MPREb_25") { Actions = _actions };
        await page.LoadAsync();

        Assert.Equal(KeptAt, page.KeptAt);
        Assert.Contains(KeptAt.ToLocalTime().ToString("d"), page.KeptNotice);
        Assert.False(page.CanSave);
        page.DownloadAllCommand.Execute(null);
        Assert.Empty(_kept.Saved);

        _catalog.Album = Album();
        await page.ReloadCommand.ExecuteAsync(null);

        Assert.Null(page.KeptNotice);
        Assert.True(page.CanSave);
        Assert.Equal(["t1", "t2"], page.Tracks.Select(t => t.VideoId));
    }

    [Fact]
    public async Task APlaylistCopy_IsReadOnly_AndALivePlaylistIsKept()
    {
        _catalog.Playlist = Playlist(KeptAt);
        var copy = new PlaylistPageViewModel(_catalog, null!, null!, _actions, "PL1");
        await copy.LoadAsync();

        Assert.NotNull(copy.KeptNotice);
        Assert.False(copy.IsEditable);
        Assert.False(copy.HasMore);
        Assert.Null(copy.DragReorderCommand);

        _catalog.Playlist = Playlist();
        var live = new PlaylistPageViewModel(_catalog, null!, null!, _actions, "PL1");
        await live.LoadAsync();
        await live.DownloadAllCommand.ExecuteAsync(null);

        var kept = Assert.Single(_kept.Saved);
        Assert.Equal((KeptCollectionKind.Playlist, "PL1", "Road trip", "Playlist • 2026", "Mira", "UCmira"),
            (kept.Kind, kept.Id, kept.Title, kept.SubtitleInfo, kept.Artist, kept.ArtistBrowseId));
    }

    [Fact]
    public async Task ALibraryTab_ShowsItsOfflineCopy_ThenNotItsRows_UnderALaterError()
    {
        var next = Task.FromResult(new LibraryPage
        {
            Items = [new PlaylistEntry { PlaylistId = "PL1", Title = "Road trip" }],
            IsOfflineCopy = true,
        });
        var section = new LibrarySection(LibrarySectionKind.Playlists, () => next, _ => throw new NotSupportedException());

        await section.EnsureLoadedAsync();

        Assert.True(section.IsOfflineCopy);
        Assert.NotNull(section.OfflineNotice);
        Assert.Single(section.Items);

        var failure = new TaskCompletionSource<LibraryPage>();
        next = failure.Task;
        // A copy isn't the library: the next visit tries the library again,
        // and the copy keeps saying what it is until that answers.
        var reload = section.EnsureLoadedAsync();
        Assert.True(section.IsOfflineCopy);
        failure.SetException(new HttpRequestException("offline"));
        await reload;

        Assert.False(section.IsOfflineCopy);
        Assert.Empty(section.Items);
        Assert.NotNull(section.LoadError);
    }
}
