using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Converters;
using Rambutan.Core.Catalog;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Downloads;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

public partial class PlaylistPageViewModel : ViewModelBase, ITrackMenuHost
{
    private readonly ICatalog _catalog;
    private readonly PlaybackCoordinator _coordinator;
    private readonly NavigationService _navigation;
    private readonly TrackActionsViewModel _actions;
    private readonly string _playlistId;
    private ContinuationToken? _continuation;
    private string? _subtitleInfo;

    [ObservableProperty] private string _title = "";
    /// <summary>Who made the playlist, ahead of <see cref="Details"/>: a link when it
    /// carries a channel. Null for a collaborative playlist, and when the subtitle
    /// already names the owner.</summary>
    [ObservableProperty] private ArtistRef? _owner;
    /// <summary>"Playlist • 2026".</summary>
    [ObservableProperty] private string _details = "";
    /// <summary>"101 songs • 7+ hours": the header's own total when it gives one,
    /// since the rows loaded so far may be only the first page.</summary>
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _thumbnailUrl;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private LoadError? _loadError;

    /// <summary>The page is the copy kept with the downloads, because the catalog
    /// couldn't be reached; null on a live page. A copy is never editable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeptNotice))]
    private DateTimeOffset? _keptAt;

    /// <summary>"Offline copy from …": says the page may be out of date and that
    /// songs not downloaded won't play.</summary>
    public string? KeptNotice => KeptAt is { } at ? Strings.Format("page.keptCopy", ("date", at.ToLocalTime().ToString("d"))) : null;

    /// <summary>The account owns this playlist, so it can be renamed or deleted and
    /// its rows removed or dragged into a new order. Other playlists' rows carry
    /// setVideoIds too, so those don't tell; signed out, nothing is editable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DragReorderCommand), nameof(ListHelp))]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(DeleteCommand), nameof(RemoveTrackCommand))]
    private bool _isEditable;

    public ObservableCollection<TrackInfo> Tracks { get; } = [];

    /// <summary>What the list hands to Controls/DragReorder: null on a playlist that
    /// can't be reordered, so a row never looks draggable and then refuses the drop.</summary>
    public IAsyncRelayCommand<ReorderRequest>? DragReorderCommand => IsEditable ? ReorderCommand : null;

    /// <summary>The list's screen-reader help, which names the move keys only where they work.</summary>
    public string? ListHelp => IsEditable ? Strings.Get("playlist.listHelp") : null;

    public PlaylistPageViewModel(
        ICatalog catalog, PlaybackCoordinator coordinator,
        NavigationService navigation, TrackActionsViewModel actions, string playlistId)
    {
        _catalog = catalog;
        _coordinator = coordinator;
        _navigation = navigation;
        _actions = actions;
        _playlistId = playlistId;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            var playlist = await _catalog.GetPlaylistAsync(_playlistId);
            // A retry from a kept copy loads over it.
            Tracks.Clear();
            KeptAt = playlist.KeptAt;
            _subtitleInfo = playlist.SubtitleInfo;
            Title = playlist.Title;
            Details = playlist.SubtitleInfo ?? "";
            Owner = OwnerOf(playlist);
            ThumbnailUrl = playlist.ThumbnailUrl;
            IsEditable = playlist.IsEditable;
            _continuation = playlist.Continuation;
            HasMore = _continuation is not null;
            foreach (var track in playlist.Tracks)
                Tracks.Add(track);
            Summary = string.IsNullOrEmpty(playlist.SecondSubtitle)
                ? DurationFormat.Summary(Tracks)
                : playlist.SecondSubtitle;
        }
        catch (Exception ex)
        {
            LoadError = LoadError.From(ex, LoadSubject.Playlist, ReloadCommand);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>The header's owner, unless the subtitle already has it as one of
    /// its parts (older headers put it there: "Playlist • Name • 2024").</summary>
    internal static ArtistRef? OwnerOf(PlaylistPage playlist) =>
        playlist.Author is { Name.Length: > 0 } author &&
        !(playlist.SubtitleInfo ?? "").Split(" • ").Contains(author.Name)
            ? author
            : null;

    /// <summary>Continuation-driven paging; the view calls this near scroll end.</summary>
    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (_continuation is null || IsLoadingMore)
            return;

        IsLoadingMore = true;
        LoadError = null;
        try
        {
            var page = await _catalog.GetPlaylistContinuationAsync(_playlistId, _continuation);
            _continuation = page.Continuation;
            HasMore = _continuation is not null;
            foreach (var track in page.Tracks)
                Tracks.Add(track);
        }
        catch (Exception ex)
        {
            LoadError = LoadError.From(ex, LoadSubject.MoreTracks, LoadMoreCommand);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    [RelayCommand]
    private Task PlayAllAsync() =>
        Tracks.Count > 0 ? _coordinator.PlayQueueAsync(Tracks.ToList()) : Task.CompletedTask;

    /// <summary>Plays the loaded tracks in a random order (like Play all, it
    /// doesn't wait for pages not yet scrolled to).</summary>
    [RelayCommand]
    private Task ShuffleAsync() =>
        Tracks.Count > 0
            ? _coordinator.PlayQueueAsync(EntryPlayback.Shuffled(Tracks, Random.Shared))
            : Task.CompletedTask;

    public bool CanDownload => _actions?.Downloads is not null;

    public bool CanCopyToLocal => _navigation?.Collections.CanCopyToLocal == true;

    /// <summary>Copies every page of the playlist, not only the rows loaded.</summary>
    [RelayCommand]
    private Task CopyToLocalAsync() =>
        _navigation.Collections.CopyToLocalCommand.ExecuteAsync(
            new PlaylistEntry { Title = Title, PlaylistId = _playlistId });

    private CollectionDownloadButton? _downloadButton;

    /// <summary>The header's Download button; null where downloads aren't offered.</summary>
    public CollectionDownloadButton? DownloadButton =>
        _downloadButton ??= _actions is { Downloads: not null }
            ? new CollectionDownloadButton(
                _actions, Tracks, DownloadTracks, DownloadAllAsync, () => Title, "playlist.downloadTip")
            : null;

    /// <summary>The playlist's tracks for its Download button. Rows not scrolled to
    /// yet are unknown, so "all downloaded" needs every page, or the copy kept with
    /// the downloads, which listed them all.</summary>
    private (IEnumerable<string> Ids, bool Complete) DownloadTracks()
    {
        var ids = Tracks.Select(t => t.VideoId);
        if (_continuation is null)
            return (ids, true);
        return _actions.KeptCollections?.Find(KeptCollectionKind.Playlist, _playlistId) is { } kept
            ? (ids.Concat(kept.Tracks.Select(t => t.VideoId)), true)
            : (ids, false);
    }

    /// <summary>Downloads the whole playlist: the pages not scrolled to yet are
    /// loaded first (bounded, in case the server ever repeats a token). A live
    /// page is kept with the downloads, so it opens offline.</summary>
    [RelayCommand]
    private async Task DownloadAllAsync()
    {
        if (_actions is null)
            return;
        for (var page = 0; page < 200 && _continuation is not null; page++)
        {
            await LoadMoreAsync();
            if (LoadError is not null)
            {
                _actions.ReportError(Strings.Format("toast.download.loadFailed", ("error", LoadError.Detail)));
                return;
            }
        }
        // Kept with the downloads once every page is in, so it opens offline.
        _actions.DownloadAll(Tracks, KeptAt is null
            ? new KeptCollection(KeptCollectionKind.Playlist, _playlistId, Title, Owner?.Name, Owner?.BrowseId,
                _subtitleInfo, ThumbnailUrl, Tracks.ToList(), DateTimeOffset.UtcNow)
            : null);
    }

    [RelayCommand]
    private Task PlayFromAsync(TrackInfo track) =>
        _coordinator.PlayQueueAsync(Tracks.ToList(), Math.Max(0, Tracks.IndexOf(track)));


    // ---- Mutations. Outcomes, failures included, are toasts: the page's
    // error slot is for loads, and a failed edit leaves the page as it was. ----

    /// <summary>"Remove from playlist" in the shared track menu, for rows of a
    /// playlist the account owns that carry a setVideoId.</summary>
    public TrackMenuExtras MenuExtrasFor(object? row, TrackInfo track) =>
        !IsEditable || track.SetVideoId is null
            ? TrackMenuExtras.None
            : new TrackMenuExtras(Edit:
                [new(Strings.Get("menu.removeFromPlaylist"), "Icon.Delete", RemoveTrackCommand, track)]);

    /// <summary>Removes at once, with Undo on the toast rather than a confirm dialog
    /// in front: the ✕ is hover-revealed and easy to hit by mistake.</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private async Task RemoveTrackAsync(TrackInfo track)
    {
        if (!IsEditable || track.SetVideoId is null)
        {
            _actions?.ReportError(Strings.Get("toast.playlist.notEditable"));
            return;
        }

        var index = Tracks.IndexOf(track);
        try
        {
            await _catalog.RemoveFromPlaylistAsync(_playlistId, track.VideoId, track.SetVideoId);
            Tracks.Remove(track);
            await RefreshSummaryAsync();
            _actions?.Toasts.Offer(Strings.Format("toast.playlist.removed", ("title", track.Title)), Strings.Get("common.undo"), () => RestoreAsync(track, index));
        }
        catch (NotSignedInException)
        {
            _actions?.ReportError(Strings.Get("toast.signInToEditPlaylists"));
        }
        catch (Exception ex)
        {
            _actions?.ReportError(Strings.Format("toast.playlist.removeFailed", ("title", track.Title), ("error", ex.Message)));
        }
    }

    /// <summary>
    /// Undo for <see cref="RemoveTrackAsync"/>: re-adds the track before whichever row
    /// now holds its old position, so it lands where it was even if other rows moved
    /// meanwhile. The restored row gets its new setVideoId, so it can be removed again.
    /// </summary>
    internal async Task RestoreAsync(TrackInfo track, int index)
    {
        var at = Math.Clamp(index, 0, Tracks.Count);
        var successor = at < Tracks.Count ? Tracks[at] : null;

        try
        {
            var setVideoId = await _catalog.InsertIntoPlaylistAsync(
                _playlistId, track.VideoId, successor?.SetVideoId);
            var restored = track with { SetVideoId = setVideoId };

            if (setVideoId is not null && successor is not null && Tracks.IndexOf(successor) is >= 0 and var before)
            {
                Tracks.Insert(before, restored);
                _actions?.Report(Strings.Format("toast.playlist.restored", ("title", track.Title)));
            }
            else if (!HasMore)
            {
                Tracks.Add(restored);
                _actions?.Report(setVideoId is null && successor is not null
                    ? Strings.Format("toast.playlist.restoredAtEnd", ("title", track.Title))
                    : Strings.Format("toast.playlist.restored", ("title", track.Title)));
            }
            else
            {
                // It went to the end of the whole playlist, past the pages not loaded
                // yet; adding it here would show it twice once paging gets there.
                _actions?.Report(Strings.Format("toast.playlist.restoredAtEnd", ("title", track.Title)));
            }
            await RefreshSummaryAsync();
        }
        catch (NotSignedInException)
        {
            _actions?.ReportError(Strings.Get("toast.signInToEditPlaylists"));
        }
        catch (Exception ex)
        {
            _actions?.ReportError(Strings.Format("toast.playlist.restoreFailed", ("title", track.Title), ("error", ex.Message)));
        }
    }

    /// <summary>
    /// The header's "12 songs • 52 minutes" after a row was removed or put back.
    /// It is YouTube Music's own text, which counts the pages not loaded
    /// yet, so the rows can recount it only when every page is loaded. Otherwise
    /// the header is read again; if that fails, the old text stays.
    /// </summary>
    private async Task RefreshSummaryAsync()
    {
        if (!HasMore)
        {
            Summary = DurationFormat.Summary(Tracks);
            return;
        }

        try
        {
            var fresh = await _catalog.GetPlaylistAsync(_playlistId);
            if (fresh.KeptAt is null && !string.IsNullOrEmpty(fresh.SecondSubtitle))
                Summary = fresh.SecondSubtitle;
        }
        catch (Exception ex)
        {
            AppLog.Write("playlist", $"couldn't reread the header of {_playlistId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Drag-reorder and Alt+↑/↓. The row moves at once and goes back if YTM refuses
    /// the move. YTM places a row before another row, never at an index, so a drop
    /// on the last loaded row while more pages exist loads the next page first:
    /// "before nothing" would mean the end of the whole playlist.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReorder))]
    private async Task ReorderAsync(ReorderRequest request)
    {
        var track = Tracks[request.From];
        var successor = PlaylistMoves.Successor(Tracks.Select(t => t.SetVideoId!).ToList(), request.From, request.To);
        Tracks.Move(request.From, request.To);

        try
        {
            if (successor is null && HasMore)
            {
                await LoadMoreAsync();
                if (LoadError is { } error)
                    throw new InvalidOperationException(error.Detail);
                var at = Tracks.IndexOf(track);
                successor = at + 1 < Tracks.Count ? Tracks[at + 1].SetVideoId : null;
                // Still nothing after it while the playlist goes on (a page already
                // loading, or one that brought no rows): don't send it to the end.
                if (successor is null && HasMore)
                    throw new InvalidOperationException(Strings.Get("playlist.moveNotLoaded"));
            }
            await _catalog.MovePlaylistItemAsync(_playlistId, track.SetVideoId!, successor);
        }
        catch (Exception ex)
        {
            if (Tracks.IndexOf(track) is >= 0 and var now)
                Tracks.Move(now, Math.Min(request.From, Tracks.Count - 1));
            _actions?.ReportError(ex is NotSignedInException
                ? Strings.Get("toast.signInToEditPlaylists")
                : Strings.Format("toast.playlist.moveFailed", ("title", track.Title), ("error", ex.Message)));
        }
    }

    private bool CanReorder(ReorderRequest? request) =>
        IsEditable && request is { From: >= 0, To: >= 0 } &&
        request.From < Tracks.Count && request.To < Tracks.Count && request.From != request.To &&
        Tracks.All(t => t.SetVideoId is not null);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private async Task RenameAsync()
    {
        if (!IsEditable || _actions?.TextPrompt is null)
            return;

        var name = await _actions.TextPrompt(Strings.Get("prompt.renamePlaylist"), Title);
        if (string.IsNullOrWhiteSpace(name) || name == Title)
            return;

        try
        {
            await _catalog.RenamePlaylistAsync(_playlistId, name);
            Title = name;
            _actions.NotifyLibraryChanged(LibrarySectionKind.Playlists);
            _actions.Report(Strings.Get("toast.playlist.renamed"));
        }
        catch (NotSignedInException)
        {
            _actions.ReportError(Strings.Get("toast.signInToEditPlaylists"));
        }
        catch (Exception ex)
        {
            _actions.ReportError(Strings.Format("toast.playlist.renameFailed", ("error", ex.Message)));
        }
    }

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private async Task DeleteAsync()
    {
        if (!IsEditable || _actions?.Confirm is null)
            return;
        if (!await _actions.Confirm(Strings.Format("confirm.deletePlaylist", ("title", Title))))
            return;

        try
        {
            await _catalog.DeletePlaylistAsync(_playlistId);
            _actions.NotifyLibraryChanged(LibrarySectionKind.Playlists);
            _actions.Report(Strings.Format("toast.playlist.deleted", ("title", Title)));
            _navigation?.Back();
        }
        catch (NotSignedInException)
        {
            _actions.ReportError(Strings.Get("toast.signInToEditPlaylists"));
        }
        catch (Exception ex)
        {
            _actions.ReportError(Strings.Format("toast.playlist.deleteFailed", ("error", ex.Message)));
        }
    }
}
