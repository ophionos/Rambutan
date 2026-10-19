using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.Core.Catalog;
using Rambutan.Core.Downloads;
using Rambutan.Core.Links;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

/// <summary>Outcome of the playlist picker: an existing playlist or a name to create.</summary>
public sealed record PlaylistPickResult(PlaylistEntry? Existing, string? NewPlaylistName);

/// <summary>
/// Track actions shared by every page (context menu, like/dislike, add-to-playlist)
/// plus the dialog delegates pages use for their own mutations. MainWindow supplies
/// the delegates; row templates bind to the commands through the window's DataContext.
/// </summary>
public partial class TrackActionsViewModel : ViewModelBase
{
    private readonly ICatalog _catalog;
    private readonly QueueModel _queue;
    private readonly PlaybackCoordinator _coordinator;
    private readonly TrackRatings _ratings;

    /// <summary>Mutation outcomes surface as toasts, never as modals.</summary>
    public ToastHostViewModel Toasts { get; }

    public Func<IReadOnlyList<PlaylistEntry>, Task<PlaylistPickResult?>>? PlaylistPicker { get; set; }
    public Func<string, string, Task<string?>>? TextPrompt { get; set; }
    public Func<string, Task<bool>>? Confirm { get; set; }

    /// <summary>Set by MainViewModel (NavigationService already depends on this VM).</summary>
    public Action<string>? OpenArtist { get; set; }

    /// <summary>Set by MainViewModel, like <see cref="OpenArtist"/>.</summary>
    public Action<string>? OpenAlbum { get; set; }

    /// <summary>Set at composition; null where downloads aren't offered (tests,
    /// design time), and the menu leaves the entry out.</summary>
    public DownloadQueue? Downloads
    {
        get => _downloads;
        set
        {
            if (_downloads is not null)
            {
                _downloads.DownloadsChanged -= OnDownloadsChanged;
                _downloads.JobsChanged -= OnJobsChanged;
                _downloads.ProgressChanged -= OnProgressChanged;
            }
            _downloads = value;
            if (value is not null)
            {
                value.DownloadsChanged += OnDownloadsChanged;
                value.JobsChanged += OnJobsChanged;
                value.ProgressChanged += OnProgressChanged;
            }
            RefreshDownloadedIds();
        }
    }

    private DownloadQueue? _downloads;

    /// <summary>The copies of downloaded albums and playlists, which "Download all"
    /// on their pages keeps; null where they aren't kept (tests, design time).</summary>
    public OfflineCollections? KeptCollections { get; init; }

    /// <summary>The videoIds that have a download whose file exists. List rows bind
    /// their own VideoId against it for the downloaded mark, as they do against the
    /// playing track for the playing one; the set is replaced, never changed in
    /// place, so each change re-evaluates those bindings.</summary>
    [ObservableProperty]
    private IReadOnlySet<string> _downloadedIds = new HashSet<string>();

    private int _refreshQueued;

    // Raised on the job's thread, once per track: a bulk removal raises hundreds,
    // which come together as one refresh.
    private void OnDownloadsChanged(string videoId)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _refreshQueued, 0);
                RefreshDownloadedIds();
            });
    }

    private void OnJobsChanged() => Dispatcher.UIThread.Post(NotifyDownloadWatchers);

    private void OnProgressChanged(DownloadJob job) => Dispatcher.UIThread.Post(NotifyDownloadWatchers);

    /// <summary>Downloads removed from a page whose Undo toast is still up: they
    /// read as gone everywhere, and their files go when the toast does.</summary>
    private readonly HashSet<string> _pendingRemoval = [];

    /// <summary>Removals whose toast has gone, being deleted in the background.</summary>
    private readonly HashSet<string> _committing = [];

    /// <summary>Runs a bulk deletion; each delete rewrites the store's manifest, so
    /// hundreds of them stay off the UI thread. Tests run it inline.</summary>
    public Func<Action, Task> RunInBackground { get; init; } = Task.Run;

    private void RefreshDownloadedIds()
    {
        DownloadedIds = _downloads is null
            ? new HashSet<string>()
            : _downloads.Downloaded.Where(d => !d.IsMissing && !IsPendingRemoval(d.Track.VideoId))
                .Select(d => d.Track.VideoId).ToHashSet();
        NotifyDownloadWatchers();
    }

    private readonly List<(WeakReference<object> Owner, Action<object> Refresh)> _downloadWatchers = [];

    /// <summary>
    /// Calls <paramref name="refresh"/> on the UI thread whenever downloads or their
    /// jobs change, for as long as <paramref name="owner"/> is alive. Pages come and
    /// go while this lives on, so the owner is held weakly: pass a <c>static</c> lambda.
    /// </summary>
    public void WhenDownloadsChange<T>(T owner, Action<T> refresh) where T : class
    {
        _downloadWatchers.RemoveAll(w => !w.Owner.TryGetTarget(out _));
        _downloadWatchers.Add((new WeakReference<object>(owner), o => refresh((T)o)));
    }

    private void NotifyDownloadWatchers()
    {
        foreach (var (owner, refresh) in _downloadWatchers.ToArray())
        {
            if (owner.TryGetTarget(out var target))
                refresh(target);
        }
    }

    /// <summary>Removed from a page: its Undo toast is still up, or its files are
    /// being deleted.</summary>
    public bool IsPendingRemoval(string videoId) =>
        _pendingRemoval.Contains(videoId) || _committing.Contains(videoId);

    /// <summary>How far a collection's downloads have got, for its page's Download button.</summary>
    public CollectionDownloadState DownloadStateOf(IEnumerable<string> videoIds, bool complete = true) =>
        Downloads is null
            ? CollectionDownloadState.Empty
            : CollectionDownloadState.Of(videoIds, DownloadedIds.Contains, Downloads.Jobs, complete);

    /// <summary>
    /// Removes an album's or playlist's downloads, with Undo. They read as gone at
    /// once, but the files are deleted only when the toast goes (or the app exits),
    /// so Undo works offline and the page's offline copy survives it.
    /// </summary>
    public void RemoveDownloads(IEnumerable<string> videoIds, string title)
    {
        if (Downloads is null)
            return;
        var ids = videoIds.Where(DownloadedIds.Contains).Distinct().ToList();
        if (ids.Count == 0)
            return;

        _pendingRemoval.UnionWith(ids);
        RefreshDownloadedIds();
        var undone = false;
        Toasts.Offer(
            Strings.Format("toast.download.removedMany", ("count", ids.Count), ("title", title)),
            Strings.Get("common.undo"),
            () =>
            {
                undone = true;
                _pendingRemoval.ExceptWith(ids);
                RefreshDownloadedIds();
                return Task.CompletedTask;
            },
            closed: () =>
            {
                if (!undone)
                    _ = CommitRemovalAsync(ids);
            });
    }

    /// <summary>Deletes every removal still waiting on its toast, here and now; call
    /// at exit, so downloads the user was told are removed don't come back next launch.</summary>
    public void FlushPendingRemovals()
    {
        foreach (var id in _pendingRemoval)
            Downloads?.Delete(id);
        _pendingRemoval.Clear();
    }

    private async Task CommitRemovalAsync(IReadOnlyList<string> ids)
    {
        // Only what is still pending: a Download all since then took its tracks back.
        // They stay hidden until their files are gone, so rows don't flash back.
        var go = ids.Where(_pendingRemoval.Remove).ToList();
        if (go.Count == 0 || Downloads is not { } downloads)
            return;
        _committing.UnionWith(go);
        try
        {
            await RunInBackground(() =>
            {
                foreach (var id in go)
                    downloads.Delete(id);
            });
        }
        catch (Exception ex)
        {
            ReportError(Strings.Format("toast.download.removeFailed", ("error", ex.Message)));
        }
        finally
        {
            _committing.ExceptWith(go);
            RefreshDownloadedIds();
        }
    }

    /// <summary>Set at composition; null where the library toggle isn't offered
    /// (tests, design time), and the menu leaves the entry out.</summary>
    public SongLibrary? SongLibrary { get; set; }

    /// <summary>Whether a song is in the library as every surface shows it; null
    /// when unknown, or when the song's row carried no way to change it.</summary>
    public bool? InLibraryOf(TrackInfo track) =>
        track.Library is null ? null : SongLibrary?.Of(track);

    /// <summary>A song was saved to the library (true) or removed (false), and
    /// YouTube Music accepted it. The Library's Songs tab drops a removed row.</summary>
    public event Action<TrackInfo, bool>? SongLibraryChanged;

    /// <summary>Set by MainWindow; clipboard access needs a TopLevel.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    // Design-time
    public TrackActionsViewModel() : this(null!, null!, null!, null!, new ToastHostViewModel()) { }

    public TrackActionsViewModel(
        ICatalog catalog, QueueModel queue, PlaybackCoordinator coordinator, TrackRatings ratings,
        ToastHostViewModel toasts)
    {
        _catalog = catalog;
        _queue = queue;
        _coordinator = coordinator;
        _ratings = ratings;
        Toasts = toasts;
    }

    public void Report(string message) => Toasts.Show(message);

    public void ReportError(string message) => Toasts.Show(message, ToastKind.Error);

    /// <summary>A save, remove or subscribe changed one Library tab; the Library
    /// reloads that tab when it is next shown.</summary>
    public event Action<LibrarySectionKind>? LibraryChanged;

    public void NotifyLibraryChanged(LibrarySectionKind kind) => LibraryChanged?.Invoke(kind);

    /// <summary>The song's rating as every thumb shows it, so the track menu agrees
    /// with the row and player-bar buttons.</summary>
    public Rating? RatingOf(TrackInfo track) => _ratings?.Of(track) ?? track.Rating;

    [RelayCommand]
    private void PlayNext(TrackInfo? track)
    {
        if (track is null)
            return;
        _queue.EnqueueNext(track);
        Report(Strings.Format("toast.track.playingNext", ("title", track.Title)));
    }

    /// <summary>A whole album or playlist, in its order, after the user's
    /// upcoming tracks; the caller says what was added.</summary>
    public void EnqueueAll(IReadOnlyList<TrackInfo> tracks) => _queue.EnqueueRange(tracks);

    [RelayCommand]
    private void AddToQueue(TrackInfo? track)
    {
        if (track is null)
            return;
        _queue.Enqueue(track);
        Report(Strings.Format("toast.track.addedToQueue", ("title", track.Title)));
    }

    [RelayCommand]
    private async Task StartRadioAsync(TrackInfo? track)
    {
        if (track is null)
            return;

        try
        {
            Report(Strings.Format("toast.mix.starting", ("title", track.Title)));
            var mix = await _catalog.GetRadioAsync(track.VideoId);
            if (mix.Count == 0)
            {
                ReportError(Strings.Get("toast.mix.empty"));
                return;
            }

            await _coordinator.PlayQueueAsync(mix);
            Report(Strings.Format("toast.mix.started", ("title", track.Title), ("count", mix.Count)));
        }
        catch (Exception ex)
        {
            ReportError(Strings.Format("toast.mix.failed", ("error", ex.Message)));
        }
    }

    /// <summary>Queues the tracks that aren't downloaded or queued yet, and says
    /// how many that was. <paramref name="page"/>, an album's or playlist's page,
    /// is kept with the downloads, so it opens offline; kept even when nothing new
    /// was queued, which refreshes an older copy.</summary>
    public void DownloadAll(IEnumerable<TrackInfo> tracks, KeptCollection? page = null)
    {
        if (Downloads is null)
            return;
        var list = tracks.ToList();
        // Downloads waiting to be removed are taken back rather than downloaded again.
        var reclaimed = list.Count(t => _pendingRemoval.Remove(t.VideoId));
        if (reclaimed > 0)
            RefreshDownloadedIds();
        var added = Downloads.Enqueue(list);
        if (page is not null)
            KeptCollections?.Keep(page);
        if (added == 0 && reclaimed == 0)
            Report(Strings.Get("toast.download.nothingNew"));
        else if (added == 0)
            Report(Strings.Format("toast.download.kept", ("count", reclaimed)));
        else if (list.Count == 1)
            Report(Strings.Format("toast.download.queued", ("title", list[0].Title)));
        else
            Report(Strings.Format("toast.download.queuedMany", ("count", added)));
    }

    [RelayCommand]
    private void Download(TrackInfo? track)
    {
        if (track is not null)
            DownloadAll([track]);
    }

    [RelayCommand]
    private void CancelDownload(TrackInfo? track)
    {
        if (track is not null)
            Downloads?.Cancel(track.VideoId);
    }

    [RelayCommand]
    private void RemoveDownload(TrackInfo? track)
    {
        if (track is null || Downloads is null)
            return;
        Downloads.Delete(track.VideoId);
        Report(Strings.Format("toast.download.removed", ("title", track.Title)));
    }

    [RelayCommand]
    private void GoToArtist(ArtistRef? artist)
    {
        // Menu items without a browseId are disabled, but the guard stays: a
        // stale menu can still fire after the queue swaps the row out.
        if (artist?.BrowseId is { } browseId)
            OpenArtist?.Invoke(browseId);
    }

    /// <summary>The player bar's title link. Tracks whose source
    /// row linked no album carry no browse id, and their title isn't a link.</summary>
    [RelayCommand]
    private void GoToAlbum(TrackInfo? track)
    {
        if (track?.AlbumBrowseId is { } browseId)
            OpenAlbum?.Invoke(browseId);
    }

    [RelayCommand]
    private async Task ShareAsync(TrackInfo? track)
    {
        if (track is null || CopyToClipboard is null)
            return;

        await CopyToClipboard(ShareLinks.ForTrack(track));
        Report(Strings.Format("toast.track.linkCopied", ("title", track.Title)));
    }

    // Concurrent executions allowed: one command instance serves every row, and
    // the default would disable every thumb on the page while one request is out.
    // TrackRatings already applies presses in order and undoes only the latest.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task LikeAsync(TrackInfo? track) => RateAsync(track, Rating.Like);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task DislikeAsync(TrackInfo? track) => RateAsync(track, Rating.Dislike);

    /// <summary>
    /// A toggle: pressing the rating a track already has clears it.
    /// The thumb changes at once; if YouTube Music refuses, it changes back and
    /// the outcome says so.
    /// </summary>
    private async Task RateAsync(TrackInfo? track, Rating pressed)
    {
        if (track is null)
            return;

        var intended = TrackRatings.Toggle(_ratings.Of(track) ?? Rating.None, pressed);
        try
        {
            var now = await _ratings.ToggleAsync(track, pressed);
            Report(now switch
            {
                Rating.Like => Strings.Format("toast.rating.liked", ("title", track.Title)),
                Rating.Dislike => Strings.Format("toast.rating.disliked", ("title", track.Title)),
                _ => pressed is Rating.Like
                    ? Strings.Format("toast.rating.likeRemoved", ("title", track.Title))
                    : Strings.Format("toast.rating.dislikeRemoved", ("title", track.Title)),
            });
        }
        catch (NotSignedInException)
        {
            ReportError(Strings.Get("toast.rating.signIn"));
        }
        catch (Exception ex)
        {
            var attempt = intended switch
            {
                Rating.Like => "like",
                Rating.Dislike => "dislike",
                _ => pressed is Rating.Like ? "removeLike" : "removeDislike",
            };
            ReportError(Strings.Format("toast.rating.failed", ("attempt", attempt), ("title", track.Title), ("error", ex.Message)));
        }
    }

    /// <summary>
    /// Saves a song to the library or removes it, with the tokens its row carried.
    /// The menu label flips at once; if YouTube Music refuses, it flips back and
    /// the outcome says so.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleInLibraryAsync(TrackInfo? track)
    {
        if (track?.Library is null || SongLibrary is null)
            return;

        try
        {
            var saved = await SongLibrary.ToggleAsync(track);
            NotifyLibraryChanged(LibrarySectionKind.Songs);
            SongLibraryChanged?.Invoke(track, saved);
            Report(Strings.Format(saved ? "toast.library.saved" : "toast.library.removed", ("title", track.Title)));
        }
        catch (NotSignedInException)
        {
            ReportError(Strings.Get("toast.library.signIn"));
        }
        catch (Exception ex)
        {
            ReportError(Strings.Format("toast.library.saveFailed", ("title", track.Title), ("error", ex.Message)));
        }
    }

    [RelayCommand]
    private async Task AddToPlaylistAsync(TrackInfo? track)
    {
        if (track is null || PlaylistPicker is null)
            return;

        try
        {
            // The picker must offer the whole library, not just the first grid
            // page (bounded in case the server ever repeats a token).
            var library = await _catalog.GetLibraryPlaylistsAsync();
            var items = new List<CatalogEntry>(library.Items);
            var continuation = library.Continuation;
            for (var page = 0; page < 20 && continuation is not null; page++)
            {
                var more = await _catalog.GetLibraryPlaylistsContinuationAsync(continuation);
                items.AddRange(more.Items);
                continuation = more.Continuation;
            }

            var playlists = items.OfType<PlaylistEntry>()
                .Where(p => p.PlaylistId != "LM") // Liked Music is rating-driven
                .ToArray();

            if (await PlaylistPicker(playlists) is not { } pick)
                return;

            string playlistId, name;
            if (pick.NewPlaylistName is { } newName)
            {
                playlistId = await _catalog.CreatePlaylistAsync(newName);
                NotifyLibraryChanged(LibrarySectionKind.Playlists);
                name = newName;
            }
            else
            {
                playlistId = pick.Existing!.PlaylistId;
                name = pick.Existing.Title;
            }

            await _catalog.AddToPlaylistAsync(playlistId, [track.VideoId]);
            Report(Strings.Format("toast.track.addedToPlaylist", ("title", track.Title), ("playlist", name)));
        }
        catch (NotSignedInException)
        {
            ReportError(Strings.Get("toast.signInToUsePlaylists"));
        }
        catch (Exception ex)
        {
            ReportError(Strings.Format("toast.track.addToPlaylistFailed", ("error", ex.Message)));
        }
    }
}
