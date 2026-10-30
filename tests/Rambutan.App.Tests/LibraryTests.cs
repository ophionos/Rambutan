using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

public sealed class LibraryTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly ToastHostViewModel _toasts = new(new ManualClock().Schedule);
    private readonly TrackActionsViewModel _actions;

    public LibraryTests() =>
        _actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, _toasts);

    private LibraryPageViewModel Library() => new(_catalog, null!, _actions, null!);

    private IEnumerable<string> Asked(string what) =>
        _catalog.LibraryCalls.Where(c => c.What == what).Select(c => c.Token?.Value ?? "first");

    [Fact]
    public async Task ShowingTheLibrary_LoadsOnlyTheTabOnScreen()
    {
        var library = Library();

        var shown = library.EnsureLoadedAsync();
        _catalog.LibraryCalls.Single().Complete(null, "Mix");
        await shown;

        Assert.Equal("playlists", Assert.Single(_catalog.LibraryCalls).What);
        Assert.Equal(["Mix"], library.Playlists.Items.Select(e => e.Title));
        Assert.True(library.IsPlaylists);
    }

    [Fact]
    public async Task ATab_LoadsWhenFirstChosen_AndOnlyThen()
    {
        var library = Library();
        var shown = library.EnsureLoadedAsync();
        _catalog.LibraryCalls[0].Complete(null, "Mix");
        await shown;

        library.SelectedSection = library.Section(LibrarySectionKind.Albums);
        _catalog.LibraryCalls[^1].Complete(null, "Discovery");
        library.SelectedSection = library.Playlists;
        library.SelectedSection = library.Section(LibrarySectionKind.Albums);

        Assert.Equal(["first"], Asked("albums"));
        Assert.Equal(["first"], Asked("playlists"));
        Assert.Equal(["Discovery"], library.SelectedSection.Items.Select(e => e.Title));
        Assert.False(library.IsPlaylists);
    }

    [Fact]
    public void ClearingTheChips_KeepsATabOnScreen()
    {
        var library = Library();

        library.SelectedSection = null!;

        Assert.Same(library.Playlists, library.SelectedSection);
    }

    [Fact]
    public void TheTabs_FollowYouTubeMusicsOrder()
    {
        Assert.Equal(
            [
                LibrarySectionKind.Playlists, LibrarySectionKind.Songs, LibrarySectionKind.Albums,
                LibrarySectionKind.Artists, LibrarySectionKind.History,
            ],
            Library().Sections.Select(s => s.Kind));
    }

    private static SongEntry Song(string id) => new()
    {
        Title = id,
        Track = new TrackInfo
        {
            VideoId = id, Title = id, Artists = [],
            Library = new LibraryToggle(true, "ADD_" + id, "REMOVE_" + id),
        },
    };

    private async Task<LibraryPageViewModel> SongsShown(params string[] ids)
    {
        var library = Library();
        library.SelectedSection = library.Songs;
        var shown = library.EnsureLoadedAsync();
        _catalog.LibraryCalls.Single().Complete(null, ids.Select(Song).ToArray<CatalogEntry>());
        await shown;
        return library;
    }

    [Fact]
    public async Task TheSongsTab_ShowsSongRows()
    {
        var library = await SongsShown("a", "b");

        Assert.True(library.IsSongs);
        Assert.Equal(["first"], Asked("songs"));
        Assert.Equal(["a", "b"], library.Songs.Items.Select(e => e.Title));
    }

    [Fact]
    public async Task ASongRemovedFromTheLibrary_LeavesTheSongsTabAtOnce()
    {
        var library = await SongsShown("a", "b", "c");
        _actions.SongLibrary = new Rambutan.Core.Catalog.SongLibrary(_catalog);
        var b = (SongEntry)library.Songs.Items[1];

        await _actions.ToggleInLibraryCommand.ExecuteAsync(b.Track);

        Assert.Equal(["remove REMOVE_b"], _catalog.LibraryEdits);
        Assert.Equal(["a", "c"], library.Songs.Items.Select(e => e.Title));
    }

    [Fact]
    public async Task ARefusedRemoval_KeepsTheRow()
    {
        var library = await SongsShown("a", "b");
        _actions.SongLibrary = new Rambutan.Core.Catalog.SongLibrary(_catalog);
        _catalog.EditFailure = new HttpRequestException("offline");

        await _actions.ToggleInLibraryCommand.ExecuteAsync(((SongEntry)library.Songs.Items[0]).Track);

        Assert.Equal(["a", "b"], library.Songs.Items.Select(e => e.Title));
    }

    [Fact]
    public async Task ASongSavedElsewhere_ReloadsTheSongsTab_WhenItIsNextShown()
    {
        var library = await SongsShown("a");
        _actions.SongLibrary = new Rambutan.Core.Catalog.SongLibrary(_catalog);
        library.SelectedSection = library.Playlists;
        _catalog.LibraryCalls[^1].Complete(null, "Mix");

        var elsewhere = new TrackInfo
        {
            VideoId = "z", Title = "z", Artists = [], Library = new LibraryToggle(false, "ADD_z", "REMOVE_z"),
        };
        await _actions.ToggleInLibraryCommand.ExecuteAsync(elsewhere);
        library.SelectedSection = library.Songs;

        Assert.Equal(["first", "first"], Asked("songs"));
    }

    [Fact]
    public void Detaching_StopsDroppingRows()
    {
        var library = Library();
        library.Songs.Items.Add(Song("a"));
        _actions.SongLibrary = new Rambutan.Core.Catalog.SongLibrary(_catalog);

        library.Detach();
        _actions.ToggleInLibraryCommand.Execute(((SongEntry)library.Songs.Items[0]).Track);

        Assert.Single(library.Songs.Items);
    }

    [Fact]
    public void ChoosingATab_BeforeTheLibraryIsShown_LoadsNothing()
    {
        var library = Library();

        library.SelectedSection = library.Section(LibrarySectionKind.Artists);

        Assert.Empty(_catalog.LibraryCalls);
    }

    [Theory]
    [InlineData(LibrarySectionKind.Albums, "albums")]
    [InlineData(LibrarySectionKind.Artists, "artists")]
    [InlineData(LibrarySectionKind.Songs, "songs")]
    public async Task ATab_LoadsItsNextPage_OntoTheEnd(LibrarySectionKind kind, string what)
    {
        var section = Library().Section(kind);
        var first = section.EnsureLoadedAsync();
        _catalog.LibraryCalls[0].Complete("t1", "A", "B");
        await first;
        Assert.True(section.HasMore);

        var more = section.LoadMoreCommand.ExecuteAsync(null);
        _catalog.LibraryCalls[^1].Complete(null, "C");
        await more;

        Assert.Equal(["first", "t1"], Asked(what));
        Assert.Equal(["A", "B", "C"], section.Items.Select(e => e.Title));
        Assert.False(section.HasMore);
    }

    [Fact]
    public async Task AFailedNextPage_KeepsTheItems_AndSaysWhatFailed()
    {
        var section = Library().Section(LibrarySectionKind.Albums);
        var first = section.EnsureLoadedAsync();
        _catalog.LibraryCalls[0].Complete("t1", "A");
        await first;

        var more = section.LoadMoreCommand.ExecuteAsync(null);
        _catalog.LibraryCalls[^1].Result.TrySetException(new System.Text.Json.JsonException()); // a title that names it
        await more;

        Assert.Equal(["A"], section.Items.Select(e => e.Title));
        Assert.NotNull(section.LoadError);
        Assert.Contains("more albums", section.LoadError!.Title);
        Assert.Same(section.LoadMoreCommand, section.LoadError.Retry);
        Assert.True(section.HasMore);
    }

    [Fact]
    public async Task SignedOut_ATabAsksToSignIn_AndTriesAgainOnTheNextVisit()
    {
        var library = Library();
        var shown = library.EnsureLoadedAsync();
        _catalog.LibraryCalls[0].FailSignedOut();
        await shown;

        Assert.True(library.Playlists.NeedsSignIn);

        var again = library.EnsureLoadedAsync();
        _catalog.LibraryCalls[^1].Complete(null, "Mix");
        await again;

        Assert.False(library.Playlists.NeedsSignIn);
        Assert.Equal(["first", "first"], Asked("playlists"));
    }

    [Fact]
    public async Task ASaveElsewhere_ReloadsOnlyThatTab_WhenItIsNextShown()
    {
        var library = Library();
        var shown = library.EnsureLoadedAsync();
        _catalog.LibraryCalls[0].Complete(null, "Mix");
        await shown;
        library.SelectedSection = library.Section(LibrarySectionKind.Albums);
        _catalog.LibraryCalls[^1].Complete(null, "Discovery");

        _actions.NotifyLibraryChanged(LibrarySectionKind.Albums);
        Assert.Equal(["first"], Asked("albums")); // nothing until it's shown

        var back = library.EnsureLoadedAsync();
        _catalog.LibraryCalls[^1].Complete(null, "Discovery", "Homework");
        await back;
        library.SelectedSection = library.Playlists;

        Assert.Equal(["first", "first"], Asked("albums"));
        Assert.Equal(["first"], Asked("playlists"));
        Assert.Equal(["Discovery", "Homework"],
            library.Section(LibrarySectionKind.Albums).Items.Select(e => e.Title));
    }

    [Fact]
    public async Task UnsubscribingFromACardOnTheTab_DropsItsCardAtOnce()
    {
        var library = Library();
        library.SelectedSection = library.Section(LibrarySectionKind.Artists);
        var shown = library.EnsureLoadedAsync();
        var daftPunk = new ArtistEntry { Title = "Daft Punk", BrowseId = "UC1" };
        _catalog.LibraryCalls[^1].Complete(null, [daftPunk, new ArtistEntry { Title = "Justice", BrowseId = "UC2" }]);
        await shown;
        var collections = new CollectionActionsViewModel(_catalog, FakePlayback.Coordinator(new QueueModel()), _actions)
        {
            IsSignedIn = () => true,
        };

        var unsubscribe = CollectionMenuModel.Build(daftPunk, collections, inLibrary: true)
            .SelectMany(g => g).Single(e => e.Header == "Unsubscribe");
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)unsubscribe.Command).ExecuteAsync(unsubscribe.Parameter);

        Assert.Equal(["unsubscribe UC1"], _catalog.LibraryEdits);
        Assert.Equal(["Justice"], library.Section(LibrarySectionKind.Artists).Items.Select(e => e.Title));
    }

    [Fact]
    public void GoingBackToTheLibrary_AfterASave_ReloadsTheChangedTab()
    {
        _catalog.Album = Album(inLibrary: false);
        var navigation = new NavigationService(_catalog, null!, _actions);
        navigation.GoLibrary();
        _catalog.LibraryCalls[0].Complete(null, "Mix");

        navigation.OpenAlbum("MPRE1");
        _actions.NotifyLibraryChanged(LibrarySectionKind.Playlists);
        navigation.Back();

        Assert.Equal(["first", "first"], Asked("playlists"));
    }

    [Fact]
    public async Task APageAskedForBeforeARefresh_IsDropped()
    {
        var section = Library().Section(LibrarySectionKind.Artists);
        var first = section.EnsureLoadedAsync();
        _catalog.LibraryCalls[0].Complete("t1", "A");
        await first;

        var more = section.LoadMoreCommand.ExecuteAsync(null);
        var stalePage = _catalog.LibraryCalls[^1];
        var refresh = section.RefreshAsync();
        _catalog.LibraryCalls[^1].Complete(null, "A", "B");
        await refresh;
        stalePage.Complete(null, "Old");
        await more;

        Assert.Equal(["A", "B"], section.Items.Select(e => e.Title));
        Assert.False(section.IsLoadingMore);
    }

    // The album and artist pages: Save and Subscribe.

    private static AlbumPage Album(bool? inLibrary, string? playlist = "OLAK1") => new()
    {
        BrowseId = "MPRE1", Title = "Discovery", AudioPlaylistId = playlist, InLibrary = inLibrary, Tracks = [],
    };

    private async Task<AlbumPageViewModel> AlbumPage(bool? inLibrary, string? playlist = "OLAK1")
    {
        _catalog.Album = Album(inLibrary, playlist);
        var page = new AlbumPageViewModel(_catalog, null!, "MPRE1") { Actions = _actions };
        await page.LoadAsync();
        return page;
    }

    [Fact]
    public async Task SavingAnAlbum_SavesItsPlaylist_AndTellsTheLibrary()
    {
        var changed = new List<LibrarySectionKind>();
        _actions.LibraryChanged += changed.Add;
        var page = await AlbumPage(inLibrary: false);
        Assert.True(page.CanSave);
        Assert.False(page.IsSaved);

        await page.ToggleLibraryCommand.ExecuteAsync(null);

        Assert.True(page.IsSaved);
        Assert.Equal(["save OLAK1"], _catalog.LibraryEdits);
        Assert.Equal([LibrarySectionKind.Albums], changed);
        Assert.Equal("Saved “Discovery” to your library", Assert.Single(_toasts.Toasts).Message);
    }

    [Fact]
    public async Task ARefusedSave_FlipsBack_AndSaysSo()
    {
        var changed = new List<LibrarySectionKind>();
        _actions.LibraryChanged += changed.Add;
        var page = await AlbumPage(inLibrary: true);
        _catalog.EditFailure = new HttpRequestException("offline");

        await page.ToggleLibraryCommand.ExecuteAsync(null);

        Assert.True(page.IsSaved);
        Assert.Empty(changed);
        var toast = Assert.Single(_toasts.Toasts);
        Assert.True(toast.IsError);
        Assert.Contains("offline", toast.Message);
    }

    [Theory]
    [InlineData(null, "OLAK1")]   // signed out: unknown, never "not saved"
    [InlineData(false, null)]     // nothing to save it by
    public async Task AnAlbumWithoutAKnownState_HasNoSaveButton(bool? inLibrary, string? playlist)
    {
        var page = await AlbumPage(inLibrary, playlist);

        Assert.False(page.CanSave);
        await page.ToggleLibraryCommand.ExecuteAsync(null);
        Assert.Empty(_catalog.LibraryEdits);
    }

    private async Task<ArtistPageViewModel> ArtistPage(bool? subscribed)
    {
        _catalog.Artist = new ArtistPage
        {
            BrowseId = "UC1", Name = "Daft Punk", Subscribed = subscribed, TopSongs = [], Shelves = [],
        };
        var page = new ArtistPageViewModel(_catalog, null!, null!, "UC1") { Actions = _actions };
        await page.LoadAsync();
        return page;
    }

    [Fact]
    public async Task Unsubscribing_TellsTheLibrary()
    {
        var changed = new List<LibrarySectionKind>();
        _actions.LibraryChanged += changed.Add;
        var page = await ArtistPage(subscribed: true);

        await page.ToggleSubscribedCommand.ExecuteAsync(null);

        Assert.False(page.Subscribed);
        Assert.Equal(["unsubscribe UC1"], _catalog.LibraryEdits);
        Assert.Equal([LibrarySectionKind.Artists], changed);
    }

    [Fact]
    public async Task ARefusedSubscribe_FlipsBack_AndSaysSo()
    {
        var page = await ArtistPage(subscribed: false);
        _catalog.EditFailure = new HttpRequestException("offline");

        await page.ToggleSubscribedCommand.ExecuteAsync(null);

        Assert.False(page.Subscribed);
        Assert.True(Assert.Single(_toasts.Toasts).IsError);
    }

    [Fact]
    public async Task AnArtistWithoutAKnownState_HasNoSubscribeButton()
    {
        var page = await ArtistPage(subscribed: null);

        Assert.False(page.CanSubscribe);
        await page.ToggleSubscribedCommand.ExecuteAsync(null);
        Assert.Empty(_catalog.LibraryEdits);
    }
}
