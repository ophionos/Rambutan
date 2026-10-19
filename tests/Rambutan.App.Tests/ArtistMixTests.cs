using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>An artist's radio, YouTube Music's Mix from the artist page's
/// header: from the page's button and from an artist card's menu.</summary>
public class ArtistMixTests
{
    private const string Channel = "UCdp";
    private static readonly MixSeed Seed = new("2NKWW42G1b8", "RDEMdp", "wAEB");
    private static readonly ArtistEntry DaftPunk = new() { Title = "Daft Punk", BrowseId = Channel };

    private readonly FakeCatalog _catalog = new();
    private readonly ToastHostViewModel _toasts = new(new ManualClock().Schedule);
    private readonly QueueModel _queue = new();
    private readonly List<MixSeed> _asked = [];

    private static TrackInfo Track(string id) => new() { VideoId = id, Title = id, Artists = [] };

    private void HasArtist(MixSeed? mix, params string[] mixIds)
    {
        _catalog.Artist = new ArtistPage
        {
            BrowseId = Channel, Name = "Daft Punk", TopSongs = [Track("top")], Shelves = [], Mix = mix,
        };
        _catalog.Mix = seed =>
        {
            _asked.Add(seed);
            return Task.FromResult<IReadOnlyList<TrackInfo>>(mixIds.Select(Track).ToArray());
        };
    }

    private TrackActionsViewModel Actions() =>
        new(_catalog, _queue, null!, new TrackRatings(_catalog), _toasts);

    private async Task<ArtistPageViewModel> PageAsync()
    {
        var page = new ArtistPageViewModel(_catalog, FakePlayback.Coordinator(_queue), null!, Channel) { Actions = Actions() };
        await page.LoadAsync();
        return page;
    }

    private CollectionActionsViewModel Cards() =>
        new(_catalog, FakePlayback.Coordinator(_queue), Actions());

    private static MenuEntry StartMix(CollectionActionsViewModel cards) =>
        CollectionMenuModel.Build(DaftPunk, cards).SelectMany(g => g).Single(e => e.Header == "Start a mix");

    [Fact]
    public async Task ThePage_PlaysTheHeadersMix_AndSaysHowManyTracks()
    {
        HasArtist(Seed, "a", "b", "c");
        var page = await PageAsync();

        Assert.True(page.CanStartMix);
        await page.StartMixCommand.ExecuteAsync(null);

        Assert.Equal([Seed], _asked);
        Assert.Equal(["a", "b", "c"], _queue.Items.Select(t => t.VideoId));
        Assert.Equal("Mix started from “Daft Punk” — 3 tracks", _toasts.Toasts.Last().Message);
    }

    [Fact]
    public async Task APageWithoutAMix_HasNoMixToStart()
    {
        HasArtist(null);
        var page = await PageAsync();

        Assert.False(page.CanStartMix);
        Assert.False(page.StartMixCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnEmptyMix_SaysSo_AndLeavesTheQueue()
    {
        HasArtist(Seed);
        _queue.Enqueue(Track("kept"));
        var page = await PageAsync();

        await page.StartMixCommand.ExecuteAsync(null);

        Assert.Equal(["kept"], _queue.Items.Select(t => t.VideoId));
        var toast = _toasts.Toasts.Last();
        Assert.True(toast.IsError);
        Assert.Equal("YouTube Music has no mix for Daft Punk.", toast.Message);
    }

    [Fact]
    public async Task ACard_LoadsThePageForItsMix_ThenPlaysIt()
    {
        HasArtist(Seed, "a", "b");
        var cards = Cards();
        var gate = new TaskCompletionSource();
        _catalog.ArtistGate = gate.Task;

        var item = StartMix(cards);
        var start = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)item.Command).ExecuteAsync(item.Parameter);
        Assert.True(cards.IsLoading(DaftPunk));
        Assert.False(StartMix(cards).IsEnabled);

        gate.SetResult();
        await start;

        Assert.False(cards.IsLoading(DaftPunk));
        Assert.Equal([Seed], _asked);
        Assert.Equal(["a", "b"], _queue.Items.Select(t => t.VideoId));
        Assert.Equal("Mix started from “Daft Punk” — 2 tracks", Assert.Single(_toasts.Toasts).Message);
    }

    [Fact]
    public async Task ACardWhosePageHasNoMix_SaysSo()
    {
        HasArtist(null);
        var cards = Cards();

        await cards.StartMixCommand.ExecuteAsync(DaftPunk);

        Assert.Empty(_asked);
        Assert.Empty(_queue.Items);
        var toast = Assert.Single(_toasts.Toasts);
        Assert.True(toast.IsError);
        Assert.Equal("YouTube Music has no mix for Daft Punk.", toast.Message);
    }

    [Fact]
    public async Task ACardWhosePageFails_SaysWhy()
    {
        // No artist set: the page fetch throws.
        var cards = Cards();

        await cards.StartMixCommand.ExecuteAsync(DaftPunk);

        Assert.False(cards.IsLoading(DaftPunk));
        var toast = Assert.Single(_toasts.Toasts);
        Assert.True(toast.IsError);
        Assert.StartsWith("Couldn't start a mix", toast.Message);
    }

    [Fact]
    public void ThePagesButton_ShowsOnlyWithAMix_AndIsNamed() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();

        Button? MixButton(MixSeed? mix)
        {
            HasArtist(mix);
            var page = PageAsync().GetAwaiter().GetResult();
            var view = new Rambutan.App.Views.ArtistPage { DataContext = page };
            var window = new Window { Width = 1000, Height = 700, DataContext = new MainViewModel(), Content = view };
            window.Show();
            window.UpdateLayout();
            var button = view.GetVisualDescendants().OfType<Button>()
                .SingleOrDefault(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Start a mix");
            window.Close();
            return button;
        }

        Assert.NotNull(MixButton(Seed));
        Assert.Null(MixButton(null));
    });
}
