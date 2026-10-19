using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

/// <summary>
/// The Library: saved playlists, saved songs, saved albums and subscribed
/// artists, one tab each. A tab loads when first shown; a save or subscribe
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
            if (_shown && _catalog is not null)
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

    // Design-time
    public LibraryPageViewModel() : this(null!, null!, null!, null!) { }

    public LibraryPageViewModel(
        ICatalog catalog, NavigationService navigation, TrackActionsViewModel actions,
        PlaybackCoordinator coordinator)
    {
        _catalog = catalog;
        _navigation = navigation;
        _actions = actions;
        _coordinator = coordinator;
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
        await SelectedSection.EnsureLoadedAsync();
    }

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
