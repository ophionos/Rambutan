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
using Rambutan.LocalLibrary;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

/// <summary>Where Library's music comes from.</summary>
public enum LibrarySource { YouTubeMusic, ThisComputer, Stats }

/// <summary>
/// The Library, from two sources. YouTube Music's: saved playlists, saved songs,
/// saved albums, subscribed artists and the account's history, one tab each. On this computer: the
/// watched folders' files (<see cref="LocalLibraryViewModel"/>), signed in or not.
/// YouTube Music's tabs: A tab loads when first shown; a save or subscribe
/// elsewhere marks its tab stale, so it reloads when next shown. A song removed
/// from the library leaves the Songs tab at once.
/// </summary>
public partial class LibraryPageViewModel : ViewModelBase, ITrackMenuHost
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

    public LibrarySection History => Section(LibrarySectionKind.History);

    /// <summary>On this computer; null where the local library isn't composed,
    /// and then the source switch doesn't show.</summary>
    public LocalLibraryViewModel? Local { get; }

    public bool HasLocal => Local is not null;

    /// <summary>Listening stats from the play log; null where it isn't composed.</summary>
    public ListeningStatsViewModel? Stats { get; }

    /// <summary>The switch shows while there is more than YouTube Music to switch to.</summary>
    public bool HasSources => Sources.Count > 1;

    public IReadOnlyList<LibrarySource> Sources { get; }

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
            OnPropertyChanged(nameof(IsStats));
            OnPropertyChanged(nameof(ShowsSpotifyImport));
            if (!_shown)
                return;
            if (IsThisComputer)
                _ = Local?.ShowAsync();
            else
                Local?.Hide();
            if (IsStats)
                _ = Stats!.LoadAsync();
            else if (IsYouTubeMusic && _catalog is not null)
                _ = SelectedSection.EnsureLoadedAsync();
        }
    }

    public bool IsYouTubeMusic => Source == LibrarySource.YouTubeMusic;

    public bool IsThisComputer => Source == LibrarySource.ThisComputer && Local is not null;

    public bool IsStats => Source == LibrarySource.Stats && Stats is not null;

    /// <summary>Import from Spotify, beside both sources: signed out, it
    /// imports to this computer.</summary>
    public bool ShowsSpotifyImport => !IsStats && _navigation?.CanImportFromSpotify == true;

    [RelayCommand]
    private void ImportFromSpotify() => _navigation.OpenSpotifyImport();

    // Design-time
    public LibraryPageViewModel() : this(null!, null!, null!, null!) { }

    /// <param name="source">The source to open on: the one the page showed
    /// before the session changed.</param>
    public LibraryPageViewModel(
        ICatalog catalog, NavigationService navigation, TrackActionsViewModel actions,
        PlaybackCoordinator coordinator, LocalLibraryViewModel? local = null,
        LibrarySource source = LibrarySource.YouTubeMusic, ListeningStatsViewModel? stats = null)
    {
        _catalog = catalog;
        _navigation = navigation;
        _actions = actions;
        _coordinator = coordinator;
        Local = local;
        Stats = stats;
        Sources = [.. Enum.GetValues<LibrarySource>().Where(s => s switch
        {
            LibrarySource.ThisComputer => local is not null,
            LibrarySource.Stats => stats is not null,
            _ => true,
        })];
        _source = Sources.Contains(source) ? source : LibrarySource.YouTubeMusic;
        Sections = Enum.GetValues<LibrarySectionKind>()
            .Select(kind => LibrarySection.For(kind, catalog, navigation is null ? null : navigation.WaitForSessionAsync))
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
        else if (IsStats)
            await Stats!.LoadAsync();
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

    /// <summary>A history song plays with the whole history as its queue, newest
    /// first, as the Songs tab's rows do.</summary>
    [RelayCommand]
    private Task PlayHistoryEntryAsync(HistoryEntry entry)
    {
        var entries = History.Items.OfType<HistoryEntry>().ToList();
        var index = entries.IndexOf(entry);
        return index >= 0
            ? _coordinator.PlayQueueAsync(entries.Select(e => e.Track).ToList(), index)
            : Task.CompletedTask;
    }

    public TrackMenuExtras MenuExtrasFor(object? row, TrackInfo track) =>
        row is HistoryEntry { RemoveToken: not null } entry
            ? new TrackMenuExtras(Edit:
                [new(Strings.Get("menu.removeFromHistory"), "Icon.Delete", RemoveFromHistoryCommand, entry)])
            : TrackMenuExtras.None;

    /// <summary>
    /// Takes the row out of the account's history at once. YouTube Music has no
    /// way to put it back, so there is no Undo; it is only in the row's menu,
    /// never a button on the row that a stray click could hit.
    /// </summary>
    [RelayCommand]
    private async Task RemoveFromHistoryAsync(HistoryEntry entry)
    {
        if (entry.RemoveToken is not { } token)
            return;
        try
        {
            await _catalog.RemoveFromHistoryAsync(token);
            History.RemoveEntry(entry);
            _actions?.Report(Strings.Format("toast.history.removed", ("title", entry.Title)));
        }
        catch (NotSignedInException)
        {
            History.NeedsSignIn = true;
        }
        catch (Exception ex)
        {
            _actions?.ReportError(Strings.Format("toast.history.removeFailed", ("title", entry.Title), ("error", ex.Message)));
        }
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

    /// <summary>Picks a playlist file to import; null when nothing was picked.
    /// Set by the view, which has the window's storage provider.</summary>
    public Func<Task<PickedFile?>>? PickPlaylistFile { get; set; }

    /// <summary>A playlist file is being read: its files' tags take a while.</summary>
    [ObservableProperty] private bool _isImportingPlaylist;

    /// <summary>
    /// Imports an M3U playlist as a new local playlist named after the file,
    /// each entry in its order (rules in <see cref="M3u"/>), and offers to open it.
    /// </summary>
    [RelayCommand]
    private async Task ImportLocalPlaylistAsync()
    {
        if (_actions is null || PickPlaylistFile is null || Local is not { PlaylistLibrary: { } library } local)
            return;
        if (library.IsReadOnly)
        {
            _actions.ReportError(TrackActionsViewModel.ReadOnlyNote(library));
            return;
        }
        if (await PickPlaylistFile() is not { } picked)
            return;
        if (picked.LocalPath is not { } path)
        {
            _actions.ReportError(Strings.Get("toast.localPlaylist.importNotLocal"));
            return;
        }

        IsImportingPlaylist = true;
        try
        {
            var read = await M3uPlaylistFiles.ReadAsync(local.Scans.Index, path);
            if (read.Items.Count == 0)
            {
                _actions.Report(Strings.Format("toast.localPlaylist.nothingToImport", ("name", read.Name)));
                return;
            }
            var imported = library.Import(read.Name, read.Items);
            var message = Strings.Format("toast.localPlaylist.imported", ("count", imported.Items.Count), ("name", imported.Name));
            if (read.Skipped > 0)
                message += " " + Strings.Format("toast.localPlaylist.importSkipped", ("count", read.Skipped));
            _actions.Toasts.Offer(message, Strings.Get("common.open"), () =>
            {
                _navigation.OpenLocalPlaylist(imported.Id);
                return Task.CompletedTask;
            });
        }
        catch (LocalPlaylistsReadOnlyException)
        {
            _actions.ReportError(TrackActionsViewModel.ReadOnlyNote(library));
        }
        catch (Exception ex)
        {
            _actions.ReportError(Strings.Format("toast.localPlaylist.importFailed",
                ("name", System.IO.Path.GetFileName(path)), ("error", ex.Message)));
        }
        finally
        {
            IsImportingPlaylist = false;
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
