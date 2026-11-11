using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.Core.Playlists;
using Rambutan.Core.Queue;
using Rambutan.Core.Sidebar;

namespace Rambutan.App.Tests;

/// <summary>Pins kept in memory, for tests that need a pin library.</summary>
internal sealed class MemorySidebarPinStore(LoadedSidebarPins? loaded = null) : ISidebarPinStore
{
    public LoadedSidebarPins Load() => loaded ?? new([]);
    public void Save(IReadOnlyList<SidebarPin> pins) { }
}

/// <summary>The sidebar's Pinned section: its rows, their states and what they do;
/// and the Pin entry of a page's header.</summary>
public class SidebarPinsViewModelTests
{
    private static readonly SidebarPin Album = new(PinKind.Album, "MPREb_a", "Discovery", "https://example.test/a.jpg");
    private static readonly SidebarPin Playlist = new(PinKind.Playlist, "PL1", "Late Night Coding");

    private readonly SidebarPinLibrary _pins = new(new MemorySidebarPinStore());
    private readonly LocalPlaylistLibrary _local = new(new MemoryPlaylists());
    private readonly List<SidebarPin> _opened = [];

    private sealed class MemoryPlaylists : ILocalPlaylistStore
    {
        public LoadedLocalPlaylists Load() => new([]);
        public void Save(IReadOnlyList<LocalPlaylist> playlists) { }
    }

    private SidebarPinsViewModel Make() => new(_pins, _local, _opened.Add, post: action => action());

    private static TrackInfo Track(string id) => new() { VideoId = id, Title = id, Artists = [] };

    private static string[][] Headers(IReadOnlyList<IReadOnlyList<MenuEntry>> groups) =>
        groups.Select(g => g.Select(e => e.Header).ToArray()).ToArray();

    [Fact]
    public void TheRows_FollowPinningUnpinningAndMoving()
    {
        var vm = Make();
        Assert.False(vm.HasPins);
        Assert.True(vm.ShowsEmpty);

        _pins.Pin(Album);
        _pins.Pin(Playlist);

        Assert.Equal(["Discovery", "Late Night Coding"], vm.Rows.Select(r => r.Title));
        Assert.True(vm.HasPins);
        Assert.False(vm.ShowsEmpty);
        var first = vm.Rows[0];
        Assert.Equal("https://example.test/a.jpg", first.CoverUrl);
        Assert.Equal("Discovery", first.AccessibleName);

        vm.MoveDownCommand.Execute(first);
        Assert.Equal(["Late Night Coding", "Discovery"], vm.Rows.Select(r => r.Title));
        // The row itself moved, so the list keeps its control and its focus.
        Assert.Same(first, vm.Rows[1]);
        Assert.Equal([Playlist, Album], _pins.Pins);

        vm.MoveUpCommand.Execute(first);
        Assert.Equal([Album, Playlist], _pins.Pins);

        vm.UnpinCommand.Execute(first);
        Assert.Equal(["Late Night Coding"], vm.Rows.Select(r => r.Title));
        Assert.Equal([Playlist], _pins.Pins);
    }

    [Fact]
    public void Open_OpensThePin()
    {
        _pins.Pin(Album);
        var vm = Make();

        vm.OpenCommand.Execute(vm.Rows[0]);

        Assert.Equal([Album], _opened);
    }

    [Fact]
    public void ALocalPlaylistsRow_ShowsItsCurrentName_AndFollowsARename()
    {
        var mine = _local.Create("Old name", [LocalPlaylistItem.Of(Track("v"))]);
        _pins.Pin(new SidebarPin(PinKind.LocalPlaylist, mine.Id, "Name when pinned"));
        var vm = Make();

        var row = Assert.Single(vm.Rows);
        Assert.Equal("Old name", row.Title);
        Assert.Equal("v", row.LocalCover?.VideoId);
        Assert.Null(row.CoverUrl);
        Assert.False(row.IsMissing);
        // The saved title follows, so it is right where the playlist can't be read.
        Assert.Equal("Old name", _pins.Pins[0].Title);

        _local.Rename(mine.Id, "New name");

        Assert.Equal("New name", Assert.Single(vm.Rows).Title);
        Assert.Equal("New name", vm.Rows[0].AccessibleName);
        Assert.Equal("New name", _pins.Pins[0].Title);
    }

    [Fact]
    public void ADeletedLocalPlaylist_StaysMissing_OpensNothing_AndStillUnpins()
    {
        var gone = _local.Create("Gone");
        _pins.Pin(new SidebarPin(PinKind.LocalPlaylist, gone.Id, "Gone"));
        var vm = Make();

        _local.Delete(gone.Id);

        var row = Assert.Single(vm.Rows);
        Assert.True(row.IsMissing);
        Assert.Equal("Gone, missing", row.AccessibleName);
        vm.OpenCommand.Execute(row);
        Assert.Empty(_opened);

        var menu = vm.MenuFor(row).SelectMany(g => g).ToList();
        Assert.False(menu.Single(e => e.Header == "Open").IsEnabled);
        var unpin = menu.Single(e => e.Header == "Unpin from sidebar");
        Assert.True(unpin.IsEnabled);
        unpin.Command.Execute(unpin.Parameter);

        Assert.Empty(vm.Rows);
        Assert.Empty(_pins.Pins);
    }

    [Fact]
    public void YouTubePins_AreNeverMissing()
    {
        _pins.Pin(Album);
        _pins.Pin(Playlist);

        Assert.All(Make().Rows, r => Assert.False(r.IsMissing));
    }

    [Fact]
    public void ARowsMenu_Opens_Moves_AndUnpins()
    {
        _pins.Pin(Album);
        _pins.Pin(Playlist);
        var vm = Make();

        var first = vm.MenuFor(vm.Rows[0]);
        Assert.Equal([["Open"], ["Move up", "Move down"], ["Unpin from sidebar"]], Headers(first));
        Assert.False(first[1][0].IsEnabled);
        Assert.True(first[1][1].IsEnabled);

        var last = vm.MenuFor(vm.Rows[1]);
        Assert.True(last[1][0].IsEnabled);
        Assert.False(last[1][1].IsEnabled);
    }

    [Fact]
    public void ReadOnlyPins_ListNone_AndSaySo()
    {
        var vm = new SidebarPinsViewModel(
            new SidebarPinLibrary(new MemorySidebarPinStore(new([], ReadOnly: true))), _local, _opened.Add, action => action());

        Assert.True(vm.IsReadOnly);
        Assert.False(vm.HasPins);
        Assert.False(vm.ShowsEmpty);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void TheRowOfThePageShowing_IsActive()
    {
        _pins.Pin(Album);
        _pins.Pin(Playlist);
        var vm = Make();
        var queue = new QueueModel();

        vm.SetCurrent(new AlbumPageViewModel(new FakeCatalog(), FakePlayback.Coordinator(queue), "MPREb_a"));
        Assert.Equal([true, false], vm.Rows.Select(r => r.IsActive));

        vm.SetCurrent(null);
        Assert.All(vm.Rows, r => Assert.False(r.IsActive));
    }

    // ---- The header's Pin entry ----

    private sealed class PageFixture
    {
        public readonly FakeCatalog Catalog = new();
        public readonly QueueModel Queue = new();
        public readonly ToastHostViewModel Toasts = new(new ManualClock().Schedule);
        public readonly SidebarPinLibrary Pins = new(new MemorySidebarPinStore());
        public readonly CollectionActionsViewModel Collections;
        public bool SidebarOpen = true;

        public PageFixture()
        {
            var actions = new TrackActionsViewModel(Catalog, Queue, null!, new TrackRatings(Catalog), Toasts);
            Collections = new CollectionActionsViewModel(Catalog, FakePlayback.Coordinator(Queue), actions)
            {
                Pins = Pins,
                IsSidebarOpen = () => SidebarOpen,
            };
        }

        public AlbumPageViewModel Album() =>
            new(Catalog, FakePlayback.Coordinator(Queue), "MPREb_a") { Collections = Collections };
    }

    [Fact]
    public async Task AnAlbumsHeader_PinsAndUnpinsIt_ByItsState()
    {
        var f = new PageFixture();
        f.Catalog.Album = new AlbumPage { BrowseId = "MPREb_a", Title = "Discovery", ThumbnailUrl = "https://art/a", Tracks = [] };
        var page = f.Album();
        Assert.False(page.Pin!.CanPin); // nothing to call it yet

        await page.LoadAsync();
        Assert.True(page.Pin.CanPin);
        Assert.False(page.Pin.IsPinned);
        Assert.Equal("Pin to sidebar", page.Pin.Name);

        page.Pin.ToggleCommand.Execute(null);
        Assert.Equal([new SidebarPin(PinKind.Album, "MPREb_a", "Discovery", "https://art/a")], f.Pins.Pins);
        Assert.True(page.Pin.IsPinned);
        Assert.Equal("Unpin from sidebar", page.Pin.Name);
        // The sidebar shows it: no toast.
        Assert.Empty(f.Toasts.Toasts);

        page.Pin.ToggleCommand.Execute(null);
        Assert.Empty(f.Pins.Pins);
    }

    [Fact]
    public async Task APinnedAlbum_TakesItsPagesTitleAndCover_WhenItLoads()
    {
        var f = new PageFixture();
        f.Pins.Pin(new SidebarPin(PinKind.Album, "MPREb_a", "Old title", "https://art/old"));
        f.Catalog.Album = new AlbumPage { BrowseId = "MPREb_a", Title = "New title", ThumbnailUrl = "https://art/new", Tracks = [] };

        await f.Album().LoadAsync();

        Assert.Equal(new SidebarPin(PinKind.Album, "MPREb_a", "New title", "https://art/new"), f.Pins.Pins[0]);
    }

    [Fact]
    public async Task WithTheSidebarCollapsed_PinningSaysSo()
    {
        var f = new PageFixture { SidebarOpen = false };
        f.Catalog.Album = new AlbumPage { BrowseId = "MPREb_a", Title = "Discovery", Tracks = [] };
        var page = f.Album();
        await page.LoadAsync();

        page.Pin!.ToggleCommand.Execute(null);
        Assert.Equal("Pinned to the sidebar", f.Toasts.Toasts[^1].Message);
        page.Pin.ToggleCommand.Execute(null);
        Assert.Equal("Unpinned from the sidebar", f.Toasts.Toasts[^1].Message);
    }

    [Fact]
    public async Task ReadOnlyPins_OfferNoHeaderEntry()
    {
        var f = new PageFixture();
        f.Collections.Pins = new SidebarPinLibrary(new MemorySidebarPinStore(new([], ReadOnly: true)));
        f.Catalog.Album = new AlbumPage { BrowseId = "MPREb_a", Title = "Discovery", Tracks = [] };
        var page = f.Album();
        await page.LoadAsync();

        Assert.False(page.Pin?.CanPin ?? false);
    }
}
