using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
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
    private readonly CollectionActionsViewModel _collections;
    private readonly List<string> _copied = [];

    private static readonly AlbumEntry Discovery = new() { Title = "Discovery", BrowseId = "MPREb_disc" };

    public CollectionMenuTests()
    {
        var actions = new TrackActionsViewModel(_catalog, _queue, null!, new TrackRatings(_catalog), _toasts)
        {
            CopyToClipboard = text =>
            {
                _copied.Add(text);
                return Task.CompletedTask;
            },
        };
        _collections = new CollectionActionsViewModel(_catalog, FakePlayback.Coordinator(_queue), actions);
    }

    private static TrackInfo Track(string id) => new() { VideoId = id, Title = id, Artists = [] };

    private void HasAlbum(params string[] ids) =>
        _catalog.Album = new AlbumPage { BrowseId = Discovery.BrowseId, Title = "Discovery", Tracks = ids.Select(Track).ToArray() };

    private static string[][] Headers(IReadOnlyList<IReadOnlyList<MenuEntry>> groups) =>
        groups.Select(g => g.Select(e => e.Header).ToArray()).ToArray();

    private MenuEntry Entry(CatalogEntry entry, string header) =>
        CollectionMenuModel.Build(entry, _collections).SelectMany(g => g).Single(e => e.Header == header);

    [Fact]
    public void AlbumsAndPlaylists_PlayShuffleQueueAndShare_ArtistsShare_SongsHaveTheTrackMenu()
    {
        string[][] playable = [["Play", "Shuffle", "Add to queue"], ["Share"]];
        Assert.Equal(playable, Headers(CollectionMenuModel.Build(Discovery, _collections)));
        Assert.Equal(playable, Headers(CollectionMenuModel.Build(
            new PlaylistEntry { Title = "Mellow", PlaylistId = "RDCLAK5uy_x" }, _collections)));
        Assert.Equal([["Share"]], Headers(CollectionMenuModel.Build(
            new ArtistEntry { Title = "Daft Punk", BrowseId = Channel }, _collections)));
        Assert.Empty(CollectionMenuModel.Build(new SongEntry { Title = "a", Track = Track("a") }, _collections));

        Assert.All(CollectionMenuModel.Build(Discovery, _collections).SelectMany(g => g), e => Assert.True(e.IsEnabled));
    }

    [Fact]
    public async Task Share_CopiesThePagesLink_AndIsDisabledWithoutOne()
    {
        var share = Entry(new ArtistEntry { Title = "Daft Punk", BrowseId = Channel }, "Share");
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
}
