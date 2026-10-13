using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>What a rambutan:// link does once parsed: it navigates, plays
/// only into an empty queue, and says why when it can't.</summary>
public class DeepLinkHandlerTests
{
    private const string Video = "khnokW3Mw24";

    private sealed class NoTimer : IDisposable
    {
        public void Dispose() { }
    }

    private readonly FakeCatalog _catalog = new();
    private readonly QueueModel _queue = new();
    private readonly ToastHostViewModel _toasts = new((_, _) => new NoTimer());
    private readonly SearchHistory _history = new(null);
    private readonly NavigationService _navigation;
    private readonly DeepLinkHandler _handler;
    private readonly Core.Playback.PlaybackCoordinator _coordinator;

    public DeepLinkHandlerTests()
    {
        _coordinator = FakePlayback.Coordinator(_queue);
        var actions = new TrackActionsViewModel(_catalog, _queue, _coordinator, null!, _toasts);
        _navigation = new NavigationService(_catalog, _coordinator, actions, _history);
        _handler = new DeepLinkHandler(_navigation, _catalog, _coordinator, _queue, actions);
    }

    private static TrackInfo Track(string id, string title) => new() { VideoId = id, Title = title, Artists = [] };

    [Fact]
    public async Task PageLinks_OpenTheirPage()
    {
        await _handler.HandleAsync("rambutan://album/MPREb_4pL8gzRtw1p");
        Assert.IsType<AlbumPageViewModel>(_navigation.Current);

        await _handler.HandleAsync("rambutan://playlist/VLPLabc");
        var playlist = Assert.IsType<PlaylistPageViewModel>(_navigation.Current);

        await _handler.HandleAsync("https://music.youtube.com/channel/UC_kRDKYrUlrbtrSiyu5Tflg");
        Assert.IsType<ArtistPageViewModel>(_navigation.Current);

        // Each link pushes a page, so Back returns through them.
        _navigation.Back();
        Assert.Same(playlist, _navigation.Current);
        Assert.Empty(_toasts.Toasts);
    }

    [Fact]
    public async Task ASearchLink_Searches_WithoutTouchingTheHistory()
    {
        await _handler.HandleAsync("rambutan://search?q=daft+punk");

        var search = Assert.IsType<SearchPageViewModel>(_navigation.Current);
        Assert.Equal("daft punk", search.Query);
        Assert.Equal("daft punk", Assert.Single(_catalog.Searches).Query);
        Assert.Empty(_history.Queries);
    }

    [Theory]
    [InlineData("rambutan://play/../../x", "Rambutan can't open this link. It isn't in a form Rambutan knows.")]
    [InlineData("rambutan://delete/PLabc", "Rambutan can't open “delete” links. A newer version may.")]
    public async Task ARefusedLink_SaysSo_InAnErrorToast(string link, string message)
    {
        await _handler.HandleAsync(link);

        var toast = Assert.Single(_toasts.Toasts);
        Assert.True(toast.IsError);
        Assert.Equal(message, toast.Message);
        Assert.Null(_navigation.Current);
    }

    [Fact]
    public async Task APlayLink_PlaysAtOnce_IntoAnEmptyQueue_WithItsRadio()
    {
        _catalog.Radio = _ => Task.FromResult<IReadOnlyList<TrackInfo>>(
            [Track(Video, "Seed"), Track("bbbbbbbbbbb", "After")]);

        await _handler.HandleAsync("rambutan://play/" + Video);

        Assert.Equal(Video, _queue.Current?.VideoId);
        Assert.Equal([Video, "bbbbbbbbbbb"], _queue.Items.Select(t => t.VideoId));
        Assert.Empty(_toasts.Toasts);
    }

    [Fact]
    public async Task APlayLink_NeverReplacesAQueue_ItGoesNext_AndPlayNowMovesToIt()
    {
        await _coordinator.PlayQueueAsync([Track("aaaaaaaaaaa", "Mine"), Track("ccccccccccc", "Also mine")]);
        _catalog.Radio = _ => Task.FromResult<IReadOnlyList<TrackInfo>>([Track(Video, "Seed")]);

        await _handler.HandleAsync("rambutan://play/" + Video);

        Assert.Equal(["aaaaaaaaaaa", Video, "ccccccccccc"], _queue.Items.Select(t => t.VideoId));
        Assert.Equal("aaaaaaaaaaa", _queue.Current?.VideoId);
        var toast = Assert.Single(_toasts.Toasts);
        Assert.Equal("“Seed” from the link plays next", toast.Message);
        Assert.Equal("Play now", toast.ActionLabel);

        await toast.ActCommand.ExecuteAsync(null);

        Assert.Equal(Video, _queue.Current?.VideoId);
        Assert.Equal(3, _queue.Items.Count);
    }

    [Fact]
    public async Task APlayLink_ForASongTheCatalogDoesNotReturn_SaysItWasNotFound()
    {
        _catalog.Radio = _ => Task.FromResult<IReadOnlyList<TrackInfo>>([Track("zzzzzzzzzzz", "Other")]);

        await _handler.HandleAsync("rambutan://play/" + Video);

        Assert.Equal("Couldn't find the linked song on YouTube Music.", Assert.Single(_toasts.Toasts).Message);
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task APlayLink_ThatCannotReachTheCatalog_SaysWhy()
    {
        _catalog.Radio = _ => Task.FromException<IReadOnlyList<TrackInfo>>(new HttpRequestException("timed out"));

        await _handler.HandleAsync("rambutan://play/" + Video);

        var toast = Assert.Single(_toasts.Toasts);
        Assert.True(toast.IsError);
        Assert.Equal("Couldn't play the linked song: timed out", toast.Message);
    }

    [Fact]
    public async Task APlainLaunch_DoesNothing_AndArgumentsAreSearchedForALink()
    {
        await _handler.HandleArgsAsync(["--profile", "work"]);
        Assert.Null(_navigation.Current);
        Assert.Empty(_toasts.Toasts);

        await _handler.HandleArgsAsync(["--deep-link", "rambutan://album/MPREb_4pL8gzRtw1p"]);
        Assert.IsType<AlbumPageViewModel>(_navigation.Current);
    }
}
