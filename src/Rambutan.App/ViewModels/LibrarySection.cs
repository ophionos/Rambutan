using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

/// <summary>The Library's tabs, in YouTube Music's order.</summary>
public enum LibrarySectionKind { Playlists, Songs, Albums, Artists }

/// <summary>
/// One Library tab: its first page, the pages after it as the list scrolls, and
/// its own loading, empty, failed and signed-out states. A tab loads the first
/// time it is shown, and again after <see cref="MarkStale"/> (a save or a
/// subscribe elsewhere changed it) the next time it is shown.
/// </summary>
public partial class LibrarySection : ViewModelBase
{
    private readonly Func<Task<LibraryPage>> _first;
    private readonly Func<ContinuationToken, Task<LibraryPage>> _next;
    private ContinuationToken? _continuation;
    private bool _loaded;
    private bool _stale;
    // Bumped by every refresh, so a page asked for before it can't land after it.
    private int _generation;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _needsSignIn;
    [ObservableProperty] private LoadError? _loadError;

    /// <summary>The library couldn't be reached, so the tab lists only the albums or
    /// playlists kept with the downloads.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfflineNotice))]
    private bool _isOfflineCopy;

    public string? OfflineNotice => !IsOfflineCopy ? null
        : Strings.Get(Kind == LibrarySectionKind.Albums ? "library.offlineAlbums" : "library.offlinePlaylists");

    public LibrarySectionKind Kind { get; }

    public ObservableCollection<CatalogEntry> Items { get; } = [];

    public LibrarySection(
        LibrarySectionKind kind,
        Func<Task<LibraryPage>> first,
        Func<ContinuationToken, Task<LibraryPage>> next)
    {
        Kind = kind;
        _first = first;
        _next = next;
    }

    /// <param name="sessionLoaded">Awaited before a first page, so a tab
    /// opened at startup asks signed in (<see cref="NavigationService.WaitForSessionAsync"/>).</param>
    public static LibrarySection For(LibrarySectionKind kind, ICatalog catalog, Func<Task>? sessionLoaded = null)
    {
        Func<Task<LibraryPage>> first = kind switch
        {
            LibrarySectionKind.Albums => () => catalog.GetLibraryAlbumsAsync(),
            LibrarySectionKind.Artists => () => catalog.GetLibraryArtistsAsync(),
            LibrarySectionKind.Songs => () => catalog.GetLibrarySongsAsync(),
            _ => () => catalog.GetLibraryPlaylistsAsync(),
        };
        Func<ContinuationToken, Task<LibraryPage>> next = kind switch
        {
            LibrarySectionKind.Albums => t => catalog.GetLibraryAlbumsContinuationAsync(t),
            LibrarySectionKind.Artists => t => catalog.GetLibraryArtistsContinuationAsync(t),
            LibrarySectionKind.Songs => t => catalog.GetLibrarySongsContinuationAsync(t),
            _ => t => catalog.GetLibraryPlaylistsContinuationAsync(t),
        };
        if (sessionLoaded is not null)
        {
            var load = first;
            first = async () =>
            {
                await sessionLoaded();
                return await load();
            };
        }
        return new(kind, first, next);
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded && !_stale)
            return;
        await RefreshAsync();
    }

    /// <summary>The library changed under this tab; it reloads when next shown.</summary>
    public void MarkStale() => _stale = true;

    /// <summary>Drops a card removed from the library from its own menu, at once.
    /// Entries are records, so an equal entry matches.</summary>
    public void RemoveEntry(CatalogEntry entry)
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Equals(Items[i], entry))
                Items.RemoveAt(i);
        }
    }

    /// <summary>Songs tab: drops a song removed from the library elsewhere, at once,
    /// as YouTube Music does; the next page's token stays good.</summary>
    public void RemoveSong(string videoId)
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i] is SongEntry song && song.Track.VideoId == videoId)
                Items.RemoveAt(i);
        }
    }

    public bool IsSongs => Kind == LibrarySectionKind.Songs;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        _loaded = true;
        _stale = false;
        IsLoading = true;
        IsLoadingMore = false;
        LoadError = null;
        NeedsSignIn = false;
        // The notice stays while a copy reloads, and its rows go if the reload fails,
        // so they never show as if they were the library.
        var wasCopy = IsOfflineCopy;
        try
        {
            var page = await _first();
            if (generation != _generation)
                return;
            Items.Clear();
            foreach (var entry in page.Items)
                Items.Add(entry);
            IsOfflineCopy = page.IsOfflineCopy;
            // A copy reloads at the next visit, so it gives way to the library once back online.
            _loaded = !page.IsOfflineCopy;
            _continuation = page.Continuation;
            HasMore = _continuation is not null;
        }
        catch (NotSignedInException)
        {
            if (generation != _generation)
                return;
            Items.Clear();
            IsOfflineCopy = false;
            NeedsSignIn = true;
            _loaded = false; // retry on next visit, e.g. after signing in
        }
        catch (Exception ex)
        {
            if (generation != _generation)
                return;
            if (wasCopy)
                Items.Clear();
            IsOfflineCopy = false;
            LoadError = LoadError.From(ex, LoadSubject.Library, RefreshCommand);
            _loaded = false;
        }
        finally
        {
            if (generation == _generation)
                IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_continuation is not { } token || IsLoadingMore || IsLoading)
            return;

        var generation = _generation;
        IsLoadingMore = true;
        LoadError = null;
        try
        {
            var page = await _next(token);
            if (generation != _generation)
                return;
            _continuation = page.Continuation;
            HasMore = _continuation is not null;
            foreach (var entry in page.Items)
                Items.Add(entry);
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                LoadError = LoadError.From(ex, MoreSubject, LoadMoreCommand);
        }
        finally
        {
            if (generation == _generation)
                IsLoadingMore = false;
        }
    }

    private LoadSubject MoreSubject => Kind switch
    {
        LibrarySectionKind.Albums => LoadSubject.MoreAlbums,
        LibrarySectionKind.Artists => LoadSubject.MoreArtists,
        LibrarySectionKind.Songs => LoadSubject.MoreSongs,
        _ => LoadSubject.MorePlaylists,
    };
}
