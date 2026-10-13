using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Services;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

/// <summary>The sidebar entry a page belongs to. Drill-down pages keep the
/// section they were opened from, the way a browser keeps the site.</summary>
public enum NavSection
{
    None,
    Home,
    Search,
    Library,
    Downloads,
    Settings,
}

/// <summary>
/// Stack navigation over the content region plus the "open a catalog entry"
/// policy: songs play, everything else navigates.
/// </summary>
public partial class NavigationService : ViewModelBase
{
    private readonly ICatalog _catalog;
    private readonly PlaybackCoordinator _coordinator;
    private readonly TrackActionsViewModel _actions;
    private readonly Stack<ViewModelBase> _backStack = new();
    private readonly Stack<ViewModelBase> _forwardStack = new();

    private HomePageViewModel? _home;
    private SearchPageViewModel? _search;
    private readonly SearchHistory? _searchHistory;
    private readonly HomeStations? _stations;
    private LibraryPageViewModel? _library;

    [ObservableProperty] private ViewModelBase? _current;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;

    public TrackActionsViewModel? Actions => _actions;

    /// <summary>Which sidebar entry reads as active. Follows every
    /// route into a page (sidebar, shortcuts, Back), not just sidebar clicks.</summary>
    [ObservableProperty] private NavSection _section;

    public NavigationService(
        ICatalog catalog, PlaybackCoordinator coordinator, TrackActionsViewModel actions,
        SearchHistory? searchHistory = null, HomeStations? stations = null)
    {
        _catalog = catalog;
        _coordinator = coordinator;
        _actions = actions;
        _searchHistory = searchHistory;
        _stations = stations;
    }

    public void GoHome()
    {
        _home ??= new HomePageViewModel(_catalog, this, _stations, (tracks, start) => _coordinator.PlayQueueAsync(tracks, start));
        NavigateRoot(_home, NavSection.Home);
        _ = _home.EnsureLoadedAsync();
    }

    public void GoSearch()
    {
        _search ??= new SearchPageViewModel(_catalog, _coordinator, this, _searchHistory);
        NavigateRoot(_search, NavSection.Search);
        _search.RequestFocus();
    }

    /// <summary>Opens Search and runs <paramref name="query"/> there, without
    /// adding it to the search history (a rambutan://search link).</summary>
    public void GoSearch(string query)
    {
        GoSearch();
        _search!.RunFromLink(query);
    }

    public void GoLibrary()
    {
        _library ??= new LibraryPageViewModel(_catalog, this, _actions, _coordinator);
        NavigateRoot(_library, NavSection.Library);
        _ = _library.EnsureLoadedAsync();
    }

    /// <summary>Opens the Downloads page; null where downloads aren't composed.
    /// Set by MainViewModel, which owns that page.</summary>
    public Action? GoDownloads { get; set; }

    public void GoTo(ViewModelBase page, NavSection section = NavSection.None) =>
        NavigateRoot(page, section);

    /// <summary>Drops the cached library page; call when the session changes.</summary>
    public void InvalidateLibrary()
    {
        if (_library is null)
            return;
        var onScreen = ReferenceEquals(Current, _library);
        _library.Detach();
        _library = null;
        // On screen, it loads again in place for the new session: signing in
        // from the Library's own prompt stays on the Library.
        if (onScreen)
            GoLibrary();
    }

    /// <summary>Requests now go out as someone else (signed in, signed out, or
    /// expired): Home's feed is the old account's. On screen it loads again now;
    /// otherwise the next time it opens.</summary>
    public void OnSignInChanged() =>
        _home?.OnSignInChanged(onScreen: ReferenceEquals(Current, _home));

    public void OpenAlbum(string browseId)
    {
        var page = new AlbumPageViewModel(_catalog, _coordinator, browseId) { Actions = _actions };
        Push(page);
        _ = page.LoadAsync();
    }

    public void OpenPlaylist(string playlistId)
    {
        var page = new PlaylistPageViewModel(_catalog, _coordinator, this, _actions, playlistId);
        Push(page);
        _ = page.LoadAsync();
    }

    /// <summary>The Moods &amp; genres grid, from Home's header.</summary>
    public void OpenMoods()
    {
        var page = new MoodsPageViewModel(_catalog, this);
        Push(page);
        _ = page.LoadAsync();
    }

    /// <summary>One mood or genre's page, from a tile or a shelf's "See all".</summary>
    public void OpenMoodCategory(MoodCategory category)
    {
        var page = new MoodCategoryPageViewModel(_catalog, this, category);
        Push(page);
        _ = page.LoadAsync();
    }

    /// <summary>A Last.fm station's page, over the station's own rows: a Home
    /// shelf's or an artist's.</summary>
    public void OpenStation(StationShelfViewModel station) => Push(new StationPageViewModel(station));

    public void OpenArtist(string browseId)
    {
        var page = new ArtistPageViewModel(_catalog, _coordinator, this, browseId)
        {
            Actions = _actions,
            Stations = _stations,
        };
        Push(page);
        _ = page.LoadAsync();
    }

    public async Task OpenEntryAsync(CatalogEntry entry)
    {
        switch (entry)
        {
            case SongEntry song:
                await PlaySongWithRadioAsync(song.Track);
                break;
            case AlbumEntry album:
                OpenAlbum(album.BrowseId);
                break;
            case PlaylistEntry playlist:
                OpenPlaylist(playlist.PlaylistId);
                break;
            case ArtistEntry artist:
                OpenArtist(artist.BrowseId);
                break;
        }
    }

    /// <summary>
    /// A card's play button: plays an album or playlist without
    /// opening it. Songs take the same route as a click. A failed fetch (an error
    /// toast) or an empty list (a plain one) says so rather than doing nothing.
    /// </summary>
    public async Task PlayEntryAsync(CatalogEntry entry)
    {
        if (entry is SongEntry song)
        {
            await PlaySongWithRadioAsync(song.Track);
            return;
        }

        try
        {
            var tracks = await EntryPlayback.ResolveAsync(_catalog, entry, _actions.DownloadedIds.Contains);
            if (tracks.Count == 0)
            {
                _actions.Report(Strings.Format("toast.nothingToPlay", ("title", entry.Title)));
                return;
            }
            await _coordinator.PlayQueueAsync(tracks);
        }
        catch (Exception ex)
        {
            _actions.ReportError(Strings.Format("toast.playFailed", ("title", entry.Title), ("error", ex.Message)));
        }
    }

    /// <summary>
    /// A lone song from a shelf (Home, Library, an artist's page) plays at once and its
    /// radio follows it, the way YTM does it.
    /// The radio is fetched alongside playback rather than before it, so the click is
    /// never slower than it was. It only extends the queue if that song still heads it.
    /// </summary>
    private async Task PlaySongWithRadioAsync(TrackInfo track)
    {
        var radio = _catalog.GetRadioAsync(track.VideoId);
        await _coordinator.PlayQueueAsync([track]);

        try
        {
            _coordinator.ExtendFromSeed(track, await radio);
        }
        catch (Exception ex)
        {
            // The song itself still plays; say why nothing will follow it.
            _actions.ReportError(Strings.Format("toast.radioFailed", ("title", track.Title), ("error", ex.Message)));
        }
    }

    [RelayCommand]
    public void Back()
    {
        if (_backStack.Count == 0)
            return;

        if (Current is not null)
            _forwardStack.Push(Current);
        Current = _backStack.Pop();
        UpdateHistoryState();
    }

    /// <summary>Undoes a Back (mouse forward button, Alt+→). The page comes back as
    /// it was left, data and scroll position included.</summary>
    [RelayCommand]
    public void Forward()
    {
        if (_forwardStack.Count == 0)
            return;

        if (Current is not null)
            _backStack.Push(Current);
        Current = _forwardStack.Pop();
        UpdateHistoryState();
    }

    /// <summary>Sidebar roots reset the history; drill-down pages push onto it.
    /// A root page opens at its top, as a sidebar click means "start here": only
    /// Back and Forward return to where a page was left.</summary>
    private void NavigateRoot(ViewModelBase page, NavSection section)
    {
        _backStack.Clear();
        _forwardStack.Clear();
        if (!ReferenceEquals(page, Current))
            page.SavedScrollOffset = 0;
        Section = section;
        Current = page;
        UpdateHistoryState();
    }

    private void Push(ViewModelBase page)
    {
        if (Current is not null)
            _backStack.Push(Current);
        _forwardStack.Clear();
        Current = page;
        UpdateHistoryState();
    }

    // Back and Forward return to the Library too; a tab a save or subscribe
    // changed meanwhile reloads then.
    partial void OnCurrentChanged(ViewModelBase? value)
    {
        if (value is not null && ReferenceEquals(value, _library))
            _ = _library.EnsureLoadedAsync();
    }

    private void UpdateHistoryState()
    {
        CanGoBack = _backStack.Count > 0;
        CanGoForward = _forwardStack.Count > 0;
    }
}
