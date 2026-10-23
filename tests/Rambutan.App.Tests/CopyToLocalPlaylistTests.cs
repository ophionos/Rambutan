using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.Core.Playlists;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>Copying a YouTube Music playlist to a playlist on this computer.</summary>
public class CopyToLocalPlaylistTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly ToastHostViewModel _toasts = new(new ManualClock().Schedule);
    private readonly List<string?> _prompted = [];
    private readonly List<string> _opened = [];
    private LocalPlaylistLibrary _library = new(new MemoryStore());
    private string? _answer = "Mellow";

    private static readonly PlaylistEntry Mellow = new() { Title = "Mellow", PlaylistId = "RDCLAK5uy_x" };

    private sealed class MemoryStore(LoadedLocalPlaylists? loaded = null) : ILocalPlaylistStore
    {
        public LoadedLocalPlaylists Load() => loaded ?? new([]);
        public void Save(IReadOnlyList<LocalPlaylist> playlists) { }
    }

    private static TrackInfo Track(string id) => new() { VideoId = id, Title = id, Artists = [] };

    private CollectionActionsViewModel Collections()
    {
        var actions = new TrackActionsViewModel(_catalog, new QueueModel(), null!, new TrackRatings(_catalog), _toasts)
        {
            LocalPlaylists = _library,
            TextPrompt = (_, initial) =>
            {
                _prompted.Add(initial);
                return Task.FromResult(_answer);
            },
        };
        return new CollectionActionsViewModel(_catalog, FakePlayback.Coordinator(new QueueModel()), actions)
        {
            OpenLocalPlaylist = _opened.Add,
        };
    }

    private void HasPlaylist(params string[][] pages)
    {
        _catalog.Playlist = new PlaylistPage
        {
            PlaylistId = Mellow.PlaylistId, Title = "Mellow (2024)", Tracks = pages[0].Select(Track).ToArray(),
            Continuation = pages.Length > 1 ? new ContinuationToken("1") : null,
        };
        _catalog.PlaylistMore = token =>
        {
            var n = int.Parse(token.Value);
            return Task.FromResult(new PlaylistPage
            {
                PlaylistId = Mellow.PlaylistId, Title = "Mellow (2024)", Tracks = pages[n].Select(Track).ToArray(),
                Continuation = n + 1 < pages.Length ? new ContinuationToken((n + 1).ToString()) : null,
            });
        };
    }

    private static string[] Headers(IReadOnlyList<IReadOnlyList<MenuEntry>> groups) =>
        groups.SelectMany(g => g).Select(e => e.Header).ToArray();

    [Fact]
    public void OnlyAPlaylistCard_OffersTheCopy_SignedOutToo()
    {
        var collections = Collections();

        Assert.Contains("Copy to a playlist on this computer", Headers(CollectionMenuModel.Build(Mellow, collections)));
        Assert.DoesNotContain("Copy to a playlist on this computer",
            Headers(CollectionMenuModel.Build(new AlbumEntry { Title = "Discovery", BrowseId = "MPREb_disc" }, collections)));
    }

    [Fact]
    public async Task TheCopy_HoldsEveryPage_UnderTheNameGiven_AndCanBeOpened()
    {
        HasPlaylist(["a", "b"], ["c", "a"]);
        _answer = "  Evening ";

        await Collections().CopyToLocalCommand.ExecuteAsync(Mellow);

        Assert.Equal(["Mellow (2024)"], _prompted);
        var copy = Assert.Single(_library.All);
        Assert.Equal("Evening", copy.Name);
        Assert.Equal(Mellow.PlaylistId, copy.CopiedFrom);
        Assert.Equal(4, copy.Items.Count);
        var toast = Assert.Single(_toasts.Toasts);
        Assert.Equal("Copied 4 songs to “Evening”", toast.Message);
        Assert.Equal("Open", toast.ActionLabel);
        await toast.ActCommand.ExecuteAsync(null);
        Assert.Equal([copy.Id], _opened);
    }

    [Fact]
    public async Task ACancelledName_CopiesNothing()
    {
        HasPlaylist(["a"]);
        _answer = null;

        await Collections().CopyToLocalCommand.ExecuteAsync(Mellow);

        Assert.Empty(_library.All);
        Assert.Empty(_toasts.Toasts);
    }

    [Fact]
    public async Task AnEmptyPlaylist_SaysSo_WithoutAskingForAName()
    {
        HasPlaylist([[]]);

        await Collections().CopyToLocalCommand.ExecuteAsync(Mellow);

        Assert.Empty(_prompted);
        Assert.Empty(_library.All);
        Assert.Equal("“Mellow” has no songs to copy.", Assert.Single(_toasts.Toasts).Message);
    }

    [Fact]
    public async Task APageThatFailsToLoad_CopiesNothing()
    {
        HasPlaylist(["a"], ["b"]);
        _catalog.PlaylistMore = _ => Task.FromException<PlaylistPage>(new HttpRequestException("offline"));

        await Collections().CopyToLocalCommand.ExecuteAsync(Mellow);

        Assert.Empty(_prompted);
        Assert.Empty(_library.All);
        var toast = Assert.Single(_toasts.Toasts);
        Assert.True(toast.IsError);
        Assert.Equal("Couldn't copy “Mellow”: offline", toast.Message);
    }

    [Fact]
    public async Task AReadOnlyFile_SaysWhy_BeforeLoadingAnything()
    {
        _library = new LocalPlaylistLibrary(new MemoryStore(new([], LocalPlaylistsProblem.Newer)));
        _catalog.PlaylistMore = _ => throw new InvalidOperationException("not loaded");

        await Collections().CopyToLocalCommand.ExecuteAsync(Mellow);

        Assert.Empty(_prompted);
        var toast = Assert.Single(_toasts.Toasts);
        Assert.True(toast.IsError);
        Assert.Equal(TrackActionsViewModel.ReadOnlyNote(_library), toast.Message);
    }
}
