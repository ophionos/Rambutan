using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Playlists;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

/// <summary>Where Library's music comes from.</summary>
public enum LibrarySource { YouTubeMusic, ThisComputer }

/// <summary>
/// The Library, from two sources. YouTube Music's: saved playlists, saved songs,
/// saved albums and subscribed artists, one tab each. On this computer: the
/// watched folders' files (<see cref="LocalLibraryViewModel"/>), signed in or not.
/// YouTube Music's tabs: A tab loads when first shown; a save or subscribe
/// elsewhere marks its tab stale, so it reloads when next shown. A song removed
/// from the library leaves the Songs tab at once.
/// </summary>
public partial class LibraryPageViewModel : ViewModelBase
{
    private readonly ICatalog _catalog;
    private readonly NavigationService _navigation;
    private readonly TrackActionsViewModel _actions;
    private readonly PlaybackCoordinator _coordinator;
    private bool _shown;

    private LibrarySection _selectedSection;

    /// <summary>The tab on screen; changing it loads the new tab if it needs to.
    /// Null (a chip list cleared by Ctrl+click) is ignored: one tab is always shown.</summary>
    public LibrarySection SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is null || !SetProperty(ref _selectedSection, value))
                return;
            OnPropertyChanged(nameof(IsPlaylists));
            OnPropertyChanged(nameof(IsSongs));
            if (_shown && _catalog is not null && IsYouTubeMusic)
                _ = value.EnsureLoadedAsync();
        }
    }

    public IReadOnlyList<LibrarySection> Sections { get; }

    public LibrarySection Playlists => Section(LibrarySectionKind.Playlists);

    /// <summary>New playlist belongs to the playlists tab only.</summary>
    public bool IsPlaylists => SelectedSection.Kind == LibrarySectionKind.Playlists;

    /// <summary>Songs show as track rows; the other tabs as cards.</summary>
    public bool IsSongs => SelectedSection.IsSongs;

    public LibrarySection Songs => Section(LibrarySectionKind.Songs);

    /// <summary>On this computer; null where the local library isn't composed,
    /// and then the source switch doesn't show.</summary>
    public LocalLibraryViewModel? Local { get; }

    public bool HasLocal => Local is not null;

    public IReadOnlyList<LibrarySource> Sources { get; } = Enum.GetValues<LibrarySource>();

    private LibrarySource _source;

    /// <summary>The source on screen. Switching to On this computer lists the
    /// files and follows the scan; switching away stops following it.</summary>
    public LibrarySource Source
    {
        get => _source;
        set
        {
            if (!SetProperty(ref _source, value))
                return;
            OnPropertyChanged(nameof(IsYouTubeMusic));
            OnPropertyChanged(nameof(IsThisComputer));
            if (!_shown)
                return;
            if (IsThisComputer)
                _ = Local?.ShowAsync();
            else
            {
                Local?.Hide();
                if (_catalog is not null)
                    _ = SelectedSection.EnsureLoadedAsync();
            }
        }
    }

    public bool IsYouTubeMusic => Source == LibrarySource.YouTubeMusic;

    public bool IsThisComputer => Source == LibrarySource.ThisComputer && Local is not null;

    // Design-time
    public LibraryPageViewModel() : this(null!, null!, null!, null!) { }

    /// <param name="source">The source to open on: the one the page showed
    /// before the session changed.</param>
    public LibraryPageViewModel(
        ICatalog catalog, NavigationService navigation, TrackActionsViewModel actions,
        PlaybackCoordinator coordinator, LocalLibraryViewModel? local = null,
        LibrarySource source = LibrarySource.YouTubeMusic)
    {
        _catalog = catalog;
        _navigation = navigation;
        _actions = actions;
        _coordinator = coordinator;
        Local = local;
        _source = local is null ? LibrarySource.YouTubeMusic : source;
        Sections = Enum.GetValues<LibrarySectionKind>()
            .Select(kind => LibrarySection.For(kind, catalog))
            .ToList();
        _selectedSection = Sections[0];
        if (actions is not null)
        {
            actions.LibraryChanged += OnLibraryChanged;
            actions.SongLibraryChanged += OnSongLibraryChanged;
            actions.RemovedFromLibrary += OnRemovedFromLibrary;
        }
    }

    private void OnLibraryChanged(LibrarySectionKind kind) => Section(kind).MarkStale();

    private void OnRemovedFromLibrary(LibrarySectionKind kind, CatalogEntry entry) =>
        Section(kind).RemoveEntry(entry);

    private void OnSongLibraryChanged(TrackInfo track, bool inLibrary)
    {
        if (!inLibrary)
            Songs.RemoveSong(track.VideoId);
    }

    /// <summary>Stops listening for library changes; navigation drops this page
    /// when the session changes.</summary>
    public void Detach()
    {
        if (_actions is not null)
        {
            _actions.LibraryChanged -= OnLibraryChanged;
            _actions.SongLibraryChanged -= OnSongLibraryChanged;
            _actions.RemovedFromLibrary -= OnRemovedFromLibrary;
        }
    }

    public LibrarySection Section(LibrarySectionKind kind) => Sections.First(s => s.Kind == kind);

    public async Task EnsureLoadedAsync()
    {
        if (_catalog is null)
            return;
        _shown = true;
        if (IsThisComputer)
            await Local!.ShowAsync();
        else
            await SelectedSection.EnsureLoadedAsync();
    }

    /// <summary>Another page took the screen: the local list stops following the scan.</summary>
    public void OnHidden()
    {
        _shown = false;
        Local?.Hide();
    }

    /// <summary>The empty state's way to the watched folders.</summary>
    [RelayCommand]
    private void OpenSettings() => _navigation.GoSettings?.Invoke();

    [RelayCommand]
    private Task OpenEntryAsync(CatalogEntry entry) => _navigation.OpenEntryAsync(entry);

    /// <summary>A card's hover play button: plays without opening the page.</summary>
    [RelayCommand]
    private Task PlayEntryAsync(CatalogEntry entry) => _navigation.PlayEntryAsync(entry);

    /// <summary>A song plays with the Songs tab's loaded rows as its queue, as a
    /// playlist's row does.</summary>
    [RelayCommand]
    private Task PlaySongAsync(SongEntry song)
    {
        var tracks = Songs.Items.OfType<SongEntry>().Select(e => e.Track).ToList();
        var index = tracks.FindIndex(t => t.VideoId == song.Track.VideoId);
        return index >= 0 ? _coordinator.PlayQueueAsync(tracks, index) : Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenLocalPlaylist(LocalPlaylistRow? row)
    {
        if (row is not null)
            _navigation.OpenLocalPlaylist(row.Playlist.Id);
    }

    /// <summary>On this computer's New playlist: an empty playlist, opened, so
    /// its page says how songs get into it.</summary>
    [RelayCommand]
    private async Task NewLocalPlaylistAsync()
    {
        if (_actions?.TextPrompt is null || Local?.PlaylistLibrary is not { } library)
            return;

        var name = await _actions.TextPrompt(Strings.Get("prompt.newPlaylist"), "");
        if (LocalPlaylists.CleanName(name) is not { } clean)
            return;

        try
        {
            var created = library.Create(clean);
            _actions.Report(Strings.Format("toast.playlist.created", ("name", created.Name)));
            _navigation.OpenLocalPlaylist(created.Id);
        }
        catch (LocalPlaylistsReadOnlyException)
        {
            _actions.ReportError(TrackActionsViewModel.ReadOnlyNote(library));
        }
        catch (Exception ex)
        {
            _actions.ReportError(Strings.Format("toast.playlist.createFailed", ("error", ex.Message)));
        }
    }

    [RelayCommand]
    private async Task NewPlaylistAsync()
    {
        if (_actions?.TextPrompt is null)
            return;

        var name = await _actions.TextPrompt(Strings.Get("prompt.newPlaylist"), "");
        if (string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            await _catalog.CreatePlaylistAsync(name);
            _actions.Report(Strings.Format("toast.playlist.created", ("name", name)));
            await Playlists.RefreshAsync();
        }
        catch (NotSignedInException)
        {
            Playlists.NeedsSignIn = true;
        }
        catch (Exception ex)
        {
            _actions.ReportError(Strings.Format("toast.playlist.createFailed", ("error", ex.Message)));
        }
    }
}
