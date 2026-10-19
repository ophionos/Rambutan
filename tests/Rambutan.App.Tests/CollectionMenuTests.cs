using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Downloads;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>The album, playlist and artist card menu, and what its entries do.</summary>
public class CollectionMenuTests
{
    private const string Channel = "UC_kRDKYrUlrbtrSiyu5Tflg";

    private readonly FakeCatalog _catalog = new();
    private readonly ToastHostViewModel _toasts = new(new ManualClock().Schedule);
    private readonly QueueModel _queue = new();
    private readonly TrackActionsViewModel _actions;
    private readonly CollectionActionsViewModel _collections;
    private readonly List<string> _copied = [];
    private readonly List<string> _opened = [];
    private readonly List<LibrarySectionKind> _changed = [];
    private readonly MemoryStore _kept = new();

    private static readonly AlbumEntry Discovery = new() { Title = "Discovery", BrowseId = "MPREb_disc" };
    private static readonly PlaylistEntry Mellow = new() { Title = "Mellow", PlaylistId = "RDCLAK5uy_x" };
    private static readonly ArtistEntry DaftPunk = new() { Title = "Daft Punk", BrowseId = Channel };

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

    public CollectionMenuTests()
    {
        _actions = new TrackActionsViewModel(_catalog, _queue, null!, new TrackRatings(_catalog), _toasts)
        {
            CopyToClipboard = text =>
            {
                _copied.Add(text);
                return Task.CompletedTask;
            },
            KeptCollections = new OfflineCollections(_kept, _ => true, _ => false),
            OpenArtist = _opened.Add,
        };
        _actions.LibraryChanged += _changed.Add;
        _collections = new CollectionActionsViewModel(_catalog, FakePlayback.Coordinator(_queue), _actions);
    }

    private void SignIn() => _collections.IsSignedIn = () => true;

    private static TrackInfo Track(string id) => new() { VideoId = id, Title = id, Artists = [] };

    private void HasAlbum(params string[] ids) =>
        _catalog.Album = new AlbumPage { BrowseId = Discovery.BrowseId, Title = "Discovery", Tracks = ids.Select(Track).ToArray() };

    private static string[][] Headers(IReadOnlyList<IReadOnlyList<MenuEntry>> groups) =>
        groups.Select(g => g.Select(e => e.Header).ToArray()).ToArray();

    private MenuEntry Entry(CatalogEntry entry, string header) =>
        CollectionMenuModel.Build(entry, _collections).SelectMany(g => g).Single(e => e.Header == header);

    private Task Run(CatalogEntry entry, string header, bool inLibrary = false)
    {
        var item = CollectionMenuModel.Build(entry, _collections, inLibrary).SelectMany(g => g).Single(e => e.Header == header);
        Assert.True(item.IsEnabled);
        return ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)item.Command).ExecuteAsync(item.Parameter);
    }

    [Fact]
    public void SignedOut_WithoutDownloads_CardsOfferPlayingAndPlaces_SongsHaveTheTrackMenu()
    {
        string[] play = ["Play", "Shuffle", "Play next", "Add to queue"];
        Assert.Equal([play, ["Go to artist", "Share"]], Headers(CollectionMenuModel.Build(Discovery, _collections)));
        Assert.Equal([play, ["Share"]], Headers(CollectionMenuModel.Build(Mellow, _collections)));
        Assert.Equal([["Play top songs", "Start a mix", "Shuffle all songs"], ["Share"]], Headers(CollectionMenuModel.Build(DaftPunk, _collections)));
        Assert.Empty(CollectionMenuModel.Build(new SongEntry { Title = "a", Track = Track("a") }, _collections));

        Assert.All(CollectionMenuModel.Build(Discovery, _collections).SelectMany(g => g), e => Assert.True(e.IsEnabled));
    }

    [Fact]
    public void SignedIn_CardsOfferToAdd_AndOnTheLibraryToRemove_ButNeverAPlaylist()
    {
        SignIn();
        _actions.Downloads = new DownloadQueue(new PendingStore());

        Assert.Equal(["Save to library", "Download"], Headers(CollectionMenuModel.Build(Discovery, _collections))[1]);
        Assert.Equal(["Remove from library", "Download"],
            Headers(CollectionMenuModel.Build(Discovery, _collections, inLibrary: true))[1]);
        Assert.Equal(["Save to library", "Download"], Headers(CollectionMenuModel.Build(Mellow, _collections))[1]);
        // A Library playlist may be the user's own, which is deleted, not removed.
        Assert.Equal(["Download"], Headers(CollectionMenuModel.Build(Mellow, _collections, inLibrary: true))[1]);
        Assert.Equal(["Subscribe"], Headers(CollectionMenuModel.Build(DaftPunk, _collections))[1]);
        Assert.Equal(["Unsubscribe"], Headers(CollectionMenuModel.Build(DaftPunk, _collections, inLibrary: true))[1]);
    }

    [Fact]
    public async Task Share_CopiesThePagesLink_AndIsDisabledWithoutOne()
    {
        var share = Entry(DaftPunk, "Share");
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)share.Command).ExecuteAsync(share.Parameter);

        Assert.Equal(["https://music.youtube.com/channel/" + Channel], _copied);
        Assert.Equal("Link to “Daft Punk” copied", _toasts.Toasts[^1].Message);

        Assert.False(Entry(new ArtistEntry { Title = "Someone", BrowseId = "FEmusic_x" }, "Share").IsEnabled);
    }

    [Fact]
    public async Task Play_PlaysTheWholeAlbumInOrder()
    {
        HasAlbum("1", "2", "3");

        await _collections.PlayCommand.ExecuteAsync(Discovery);

        Assert.Equal(["1", "2", "3"], _queue.Items.Select(t => t.VideoId));
        Assert.Equal("1", _queue.Current?.VideoId);
    }

    [Fact]
    public async Task Shuffle_PlaysEveryTrack()
    {
        HasAlbum("1", "2", "3", "4", "5");

        await _collections.ShuffleCommand.ExecuteAsync(Discovery);

        Assert.Equal(["1", "2", "3", "4", "5"], _queue.Items.Select(t => t.VideoId).Order());
    }

    [Fact]
    public async Task AddToQueue_QueuesTheAlbumAfterWhatIsQueued_AndSaysHowMany()
    {
        _queue.SetQueue([Track("playing")]);
        HasAlbum("1", "2", "3");

        await _collections.AddToQueueCommand.ExecuteAsync(Discovery);

        Assert.Equal(["playing", "1", "2", "3"], _queue.Items.Select(t => t.VideoId));
        Assert.Equal("playing", _queue.Current?.VideoId); // nothing interrupted
        Assert.Equal("Added 3 songs from “Discovery” to the queue", _toasts.Toasts[^1].Message);
    }

    [Fact]
    public async Task PlayNext_PutsTheAlbumRightAfterTheCurrentSong_InOrder()
    {
        _queue.SetQueue([Track("playing"), Track("later")]);
        HasAlbum("1", "2");

        await Run(Discovery, "Play next");

        Assert.Equal(["playing", "1", "2", "later"], _queue.Items.Select(t => t.VideoId));
        Assert.Equal("playing", _queue.Current?.VideoId);
        Assert.Equal("2 songs from “Discovery” play next", _toasts.Toasts[^1].Message);
    }

    [Fact]
    public async Task PlayTopSongs_PlaysTheArtistsTopSongs()
    {
        _catalog.Artist = new ArtistPage { BrowseId = Channel, Name = "Daft Punk", TopSongs = [Track("a"), Track("b")], Shelves = [] };

        await Run(DaftPunk, "Play top songs");

        Assert.Equal(["a", "b"], _queue.Items.Select(t => t.VideoId));
        Assert.False(_collections.IsLoading(DaftPunk));
    }

    [Fact]
    public async Task WhileTheAlbumLoads_EveryCardShowsIt_AndItsEntriesWait()
    {
        HasAlbum("1", "2");
        var gate = new TaskCompletionSource();
        _catalog.AlbumGate = gate.Task;
        var changes = 0;
        _collections.LoadingChanged += () => changes++;

        var play = _collections.PlayCommand.ExecuteAsync(Discovery);

        // Another card showing the same album, an equal entry of its own.
        Assert.True(_collections.IsLoading(Discovery with { }));
        Assert.All(CollectionMenuModel.Build(Discovery, _collections)[0], e => Assert.False(e.IsEnabled));
        Assert.False(Entry(Discovery, "Go to artist").IsEnabled);
        Assert.True(Entry(Discovery, "Share").IsEnabled);
        Assert.False(_collections.IsLoading(new AlbumEntry { Title = "Other", BrowseId = "MPREb_other" }));

        // A second request while it loads is dropped, not queued behind it.
        await _collections.AddToQueueCommand.ExecuteAsync(Discovery);

        gate.SetResult();
        await play;

        Assert.False(_collections.IsLoading(Discovery));
        Assert.Equal(2, changes);
        Assert.Equal(["1", "2"], _queue.Items.Select(t => t.VideoId));
    }

    [Fact]
    public async Task AFailedLoad_EndsTheLoading_AndSaysWhy()
    {
        // FakeCatalog has no album set, so the fetch throws.
        await _collections.AddToQueueCommand.ExecuteAsync(Discovery);

        Assert.False(_collections.IsLoading(Discovery));
        var toast = Assert.Single(_toasts.Toasts);
        Assert.True(toast.IsError);
        Assert.StartsWith("Couldn't add “Discovery” to the queue", toast.Message);
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task AnEmptyAlbum_SaysSo_AndQueuesNothing()
    {
        HasAlbum();

        await _collections.AddToQueueCommand.ExecuteAsync(Discovery);

        Assert.Equal("Nothing to play in “Discovery”.", Assert.Single(_toasts.Toasts).Message);
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task SavingAnAlbumWhoseCardHasNoPlaylist_LoadsItFromThePage()
    {
        SignIn();
        _catalog.Album = new AlbumPage { BrowseId = Discovery.BrowseId, Title = "Discovery", AudioPlaylistId = "OLAK5uy_disc", Tracks = [] };

        await Run(Discovery, "Save to library");

        Assert.Equal(["save OLAK5uy_disc"], _catalog.LibraryEdits);
        Assert.Equal([LibrarySectionKind.Albums], _changed);
        Assert.Equal("Saved “Discovery” to your library", _toasts.Toasts[^1].Message);
    }

    [Fact]
    public async Task SavingAPlaylist_AndUnsubscribing_SayWhatChanged()
    {
        SignIn();

        await Run(Mellow, "Save to library");
        await Run(DaftPunk, "Unsubscribe", inLibrary: true);

        Assert.Equal(["save playlist RDCLAK5uy_x", "unsubscribe " + Channel], _catalog.LibraryEdits);
        Assert.Equal([LibrarySectionKind.Playlists, LibrarySectionKind.Artists], _changed);
        Assert.Equal("Unsubscribed from Daft Punk", _toasts.Toasts[^1].Message);
    }

    [Fact]
    public async Task ARefusedSave_SaysWhy_AndEndsTheLoading()
    {
        SignIn();
        _catalog.EditFailure = new InvalidOperationException("refused");

        await Run(DaftPunk, "Subscribe");

        var toast = Assert.Single(_toasts.Toasts);
        Assert.True(toast.IsError);
        Assert.Equal("Couldn't change your subscription to Daft Punk: refused", toast.Message);
        Assert.False(_collections.IsLoading(DaftPunk));
        Assert.Empty(_changed);
    }

    [Fact]
    public async Task GoToArtist_OpensTheArtistThePageNames_OrSaysItNamesNone()
    {
        _catalog.Album = new AlbumPage { BrowseId = Discovery.BrowseId, Title = "Discovery", ArtistBrowseId = Channel, Tracks = [] };
        await Run(Discovery, "Go to artist");
        Assert.Equal([Channel], _opened);

        _catalog.Album = _catalog.Album with { ArtistBrowseId = null };
        await Run(Discovery, "Go to artist");
        Assert.Single(_opened);
        Assert.Equal("“Discovery” doesn't name its artist", _toasts.Toasts[^1].Message);
    }

    [Fact]
    public async Task DownloadingAnAlbum_KeepsItsPage_WithTheHeadersArt()
    {
        var downloads = new DownloadQueue(new PendingStore());
        _actions.Downloads = downloads;
        _catalog.Album = new AlbumPage
        {
            BrowseId = Discovery.BrowseId, Title = "Discovery", Artist = "Daft Punk", ArtistBrowseId = Channel,
            ThumbnailUrl = "https://art/disc", Tracks = [Track("1"), Track("2")],
        };

        await Run(Discovery, "Download");

        Assert.Equal(["1", "2"], downloads.Jobs.Select(j => j.Track.VideoId));
        var kept = Assert.Single(_kept.Saved);
        Assert.Equal((KeptCollectionKind.Album, "MPREb_disc", "Daft Punk"), (kept.Kind, kept.Id, kept.Artist));
        Assert.All(kept.Tracks, t => Assert.Equal("https://art/disc", t.ThumbnailUrl));
    }

    [Fact]
    public async Task DownloadingAPlaylist_LoadsEveryPageFirst()
    {
        var downloads = new DownloadQueue(new PendingStore());
        _actions.Downloads = downloads;
        _catalog.Playlist = new PlaylistPage
        {
            PlaylistId = Mellow.PlaylistId, Title = "Mellow", Tracks = [Track("1")], Continuation = new ContinuationToken("p2"),
        };
        _catalog.PlaylistMore = token => Task.FromResult(new PlaylistPage
        {
            PlaylistId = Mellow.PlaylistId, Title = "Mellow",
            Tracks = token.Value == "p2" ? [Track("2")] : [Track("3")],
            Continuation = token.Value == "p2" ? new ContinuationToken("p3") : null,
        });

        await Run(Mellow, "Download");

        Assert.Equal(["1", "2", "3"], downloads.Jobs.Select(j => j.Track.VideoId));
        Assert.Equal(["1", "2", "3"], Assert.Single(_kept.Saved).Tracks.Select(t => t.VideoId));
    }

    [Fact]
    public async Task APlaylistPageThatFailsToLoad_DownloadsNothing()
    {
        var downloads = new DownloadQueue(new PendingStore());
        _actions.Downloads = downloads;
        _catalog.Playlist = new PlaylistPage
        {
            PlaylistId = Mellow.PlaylistId, Title = "Mellow", Tracks = [Track("1")], Continuation = new ContinuationToken("p2"),
        };
        _catalog.PlaylistMore = _ => Task.FromException<PlaylistPage>(new HttpRequestException("offline"));

        await Run(Mellow, "Download");

        Assert.Empty(downloads.Jobs);
        Assert.Empty(_kept.Saved);
        Assert.Equal("Couldn't load every song to download: offline", Assert.Single(_toasts.Toasts).Message);
    }
}
