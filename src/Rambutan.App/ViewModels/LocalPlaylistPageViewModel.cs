using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Controls;
using Rambutan.App.Converters;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Playlists;

namespace Rambutan.App.ViewModels;

/// <summary>A row of a playlist on this computer, as it shows now
/// (<see cref="LocalPlaylists.Resolve"/>). A copy can hold a song twice, so a
/// row is its place in the list, not its id: a class, so two rows of one song
/// are never equal and the list finds the one that was acted on.</summary>
public sealed class LocalPlaylistTrackRow(LocalPlaylistItem item, bool isMissing) : IHasAccessibleName, ITrackRow
{
    // The name never changes: a changed row is a new row.
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public LocalPlaylistItem Item { get; } = item;

    /// <summary>A local file that is gone and wasn't found again.</summary>
    public bool IsMissing { get; } = isMissing;

    public TrackInfo Track => Item.Track;
    public string Title => Track.Title;

    /// <summary>The dimming is visual only, so a missing file goes into the name.</summary>
    public string AccessibleName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Track.ArtistDisplay)
                ? Title
                : Strings.Format("queue.row.nameWithArtist", ("title", Title), ("artist", Track.ArtistDisplay));
            return IsMissing ? Strings.Format("localPlaylist.row.missing", ("name", name)) : name;
        }
    }
}

/// <summary>
/// A playlist on this computer: its rows with the files found again where they
/// moved, and its edits, which work as on the account's own YouTube Music
/// playlist (maintainer's decision). An edit shows at once and is saved whole;
/// one that can't be saved is a toast, and the page shows what is kept. While
/// it shows, it follows the playlists' file and the scans.
/// </summary>
public partial class LocalPlaylistPageViewModel : ViewModelBase, ITrackMenuHost
{
    private readonly LocalPlaylistLibrary _library;
    private readonly LocalLibraryScans _scans;
    private readonly PlaybackCoordinator? _coordinator;
    private readonly NavigationService? _navigation;
    private readonly TrackActionsViewModel _actions;
    private readonly Action<Action> _post;
    private bool _shown;
    private Task? _refresh;
    private bool _refreshAgain;

    public string Id { get; }

    [ObservableProperty] private string _title = "";

    /// <summary>"12 songs • 48 min"; empty while there are no rows.</summary>
    [ObservableProperty] private string _summary = "";

    /// <summary>The first row, whose cover heads the page.</summary>
    [ObservableProperty] private TrackInfo? _cover;

    [ObservableProperty] private bool _isLoading = true;

    /// <summary>The playlist was deleted (here, or in another window of the app's
    /// data folder): the page says so instead of showing nothing.</summary>
    [ObservableProperty] private bool _isGone;

    /// <summary>It holds nothing yet: the page says how songs get into it.</summary>
    [ObservableProperty] private bool _isEmpty;

    /// <summary>Some row plays: Play all and Shuffle work.</summary>
    [ObservableProperty] private bool _hasPlayable;

    /// <summary>The playlists' file can't be changed here (a newer build's, or
    /// unreadable and left in place): no edits, and the reason shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable), nameof(DragReorderCommand), nameof(ListHelp))]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(DeleteCommand), nameof(RemoveRowCommand))]
    private string? _readOnlyNote;

    public bool IsEditable => ReadOnlyNote is null;

    public ObservableCollection<LocalPlaylistTrackRow> Rows { get; } = [];

    /// <summary>What the list hands to Controls/DragReorder: null when the rows
    /// can't move, so a row never looks draggable and then refuses the drop.</summary>
    public IRelayCommand<ReorderRequest>? DragReorderCommand => IsEditable ? ReorderCommand : null;

    /// <summary>The list's screen-reader help, which names the move keys only where they work.</summary>
    public string? ListHelp => IsEditable ? Strings.Get("playlist.listHelp") : null;

    /// <param name="post">Runs on the UI thread; tests pass their own.</param>
    public LocalPlaylistPageViewModel(
        LocalPlaylistLibrary library, string id, LocalLibraryScans scans, PlaybackCoordinator? coordinator,
        NavigationService? navigation, TrackActionsViewModel actions, Action<Action>? post = null)
    {
        _library = library;
        Id = id;
        _scans = scans;
        _coordinator = coordinator;
        _navigation = navigation;
        _actions = actions;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
    }

    /// <summary>The page is on screen: reads the playlist again and follows changes. UI thread only.</summary>
    public Task ShowAsync()
    {
        if (!_shown)
        {
            _shown = true;
            _library.Changed += OnChanged;
            _scans.Changed += OnChanged;
        }
        return RefreshAsync();
    }

    public void Hide()
    {
        if (!_shown)
            return;
        _shown = false;
        _library.Changed -= OnChanged;
        _scans.Changed -= OnChanged;
    }

    // Either may be raised off the UI thread.
    private void OnChanged() => _post(() =>
    {
        if (_shown)
            _ = RefreshAsync();
    });

    /// <summary>Reads the playlist and finds its files off the UI thread, then
    /// edits the rows to match. One asked for while one runs makes it run once
    /// more. UI thread only.</summary>
    internal Task RefreshAsync()
    {
        if (_refresh is { IsCompleted: false })
        {
            _refreshAgain = true;
            return _refresh;
        }
        return _refresh = RunRefreshAsync();
    }

    private async Task RunRefreshAsync()
    {
        do
        {
            _refreshAgain = false;
            var index = _scans.Index;
            var (playlist, rows) = await Task.Run<(LocalPlaylist?, List<LocalPlaylistTrackRow>)>(() =>
            {
                if (_library.Find(Id) is not { } found)
                    return (null, []);
                // Downloads play under their YouTube ids, so their files are never a row's.
                var present = index.Files().Where(f => f is { IsMissing: false, IsUnreadable: false, Download: null }).ToList();
                var (resolved, changed) = LocalPlaylists.ResolveAll(found, index.Find, present);
                if (changed is not null)
                    _library.KeepResolved(changed);
                return (found, resolved.Select(r => new LocalPlaylistTrackRow(r.Item, r.IsMissing)).ToList());
            });
            Apply(rows);
            IsGone = playlist is null;
            if (playlist is not null)
                Title = playlist.Name;
            ReadOnlyNote = _library.IsReadOnly ? TrackActionsViewModel.ReadOnlyNote(_library) : null;
            IsLoading = false;
            UpdateSummary();
        } while (_refreshAgain);
    }

    /// <summary>Replaces only the rows that changed, so a reorder or a remove the
    /// page already made keeps the focus where it is.</summary>
    private void Apply(IReadOnlyList<LocalPlaylistTrackRow> wanted)
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i == Rows.Count)
                Rows.Add(wanted[i]);
            else if (!SameRow(Rows[i], wanted[i]))
                Rows[i] = wanted[i];
        }
        while (Rows.Count > wanted.Count)
            Rows.RemoveAt(Rows.Count - 1);
    }

    // A found file's item is made again on each read, so the row is compared by
    // what it shows.
    private static bool SameRow(LocalPlaylistTrackRow a, LocalPlaylistTrackRow b) =>
        a.IsMissing == b.IsMissing && a.Item.Path == b.Item.Path &&
        a.Track.VideoId == b.Track.VideoId && a.Track.Title == b.Track.Title &&
        a.Track.Album == b.Track.Album && a.Track.Duration == b.Track.Duration &&
        a.Track.ArtistDisplay == b.Track.ArtistDisplay;

    private void UpdateSummary()
    {
        Summary = DurationFormat.Summary(Rows.Select(r => r.Track).ToList());
        Cover = Rows.FirstOrDefault()?.Track;
        IsEmpty = !IsGone && !IsLoading && Rows.Count == 0;
        HasPlayable = Rows.Any(r => !r.IsMissing);
    }

    private List<TrackInfo> Playable() => Rows.Where(r => !r.IsMissing).Select(r => r.Track).ToList();

    [RelayCommand]
    private Task PlayAllAsync() =>
        _coordinator is not null && Playable() is { Count: > 0 } tracks
            ? _coordinator.PlayQueueAsync(tracks)
            : Task.CompletedTask;

    [RelayCommand]
    private Task ShuffleAsync() =>
        _coordinator is not null && Playable() is { Count: > 0 } tracks
            ? _coordinator.PlayQueueAsync(EntryPlayback.Shuffled(tracks, Random.Shared))
            : Task.CompletedTask;

    /// <summary>A row plays with the rows that play as its queue; a missing
    /// file says why it doesn't.</summary>
    [RelayCommand]
    private Task PlayFromAsync(LocalPlaylistTrackRow? row)
    {
        if (row is null || _coordinator is null)
            return Task.CompletedTask;
        if (row.IsMissing)
        {
            _actions.ReportError(Strings.Format("toast.localPlaylist.missing", ("title", row.Title)));
            return Task.CompletedTask;
        }
        var playable = Rows.Where(r => !r.IsMissing).ToList();
        return _coordinator.PlayQueueAsync(playable.Select(r => r.Track).ToList(), Math.Max(0, playable.IndexOf(row)));
    }

    // ---- Edits. Outcomes, failures included, are toasts, as on a YouTube Music playlist. ----

    public TrackMenuExtras MenuExtrasFor(object? row, TrackInfo track) =>
        !IsEditable || row is not LocalPlaylistTrackRow listed
            ? TrackMenuExtras.None
            : new TrackMenuExtras(Edit:
                [new(Strings.Get("menu.removeFromPlaylist"), "Icon.Delete", RemoveRowCommand, listed)]);

    /// <summary>Removes at once, with Undo on the toast, as a YouTube Music
    /// playlist's row does.</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void RemoveRow(LocalPlaylistTrackRow? row)
    {
        if (row is null || Rows.IndexOf(row) is not (>= 0 and var index))
            return;
        if (!Edit(() => _library.RemoveAt(Id, index), error => Strings.Format("toast.playlist.removeFailed", ("title", row.Title), ("error", error))))
            return;
        Rows.RemoveAt(index);
        UpdateSummary();
        _actions.Toasts.Offer(Strings.Format("toast.playlist.removed", ("title", row.Title)), Strings.Get("common.undo"),
            () =>
            {
                if (Edit(() => _library.Insert(Id, index, row.Item), error => Strings.Format("toast.playlist.restoreFailed", ("title", row.Title), ("error", error))))
                    _actions.Report(Strings.Format("toast.playlist.restored", ("title", row.Title)));
                return Task.CompletedTask;
            });
    }

    /// <summary>Drag-reorder and Alt+↑/↓. The row moves at once and goes back if
    /// the move can't be saved.</summary>
    [RelayCommand(CanExecute = nameof(CanReorder))]
    private void Reorder(ReorderRequest request)
    {
        var row = Rows[request.From];
        Rows.Move(request.From, request.To);
        if (!Edit(() => _library.Move(Id, request.From, request.To), error => Strings.Format("toast.playlist.moveFailed", ("title", row.Title), ("error", error))))
            Rows.Move(request.To, request.From);
        UpdateSummary();
    }

    private bool CanReorder(ReorderRequest? request) =>
        IsEditable && request is { From: >= 0, To: >= 0 } &&
        request.From < Rows.Count && request.To < Rows.Count && request.From != request.To;

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private async Task RenameAsync()
    {
        if (_actions.TextPrompt is null)
            return;
        var name = LocalPlaylists.CleanName(await _actions.TextPrompt(Strings.Get("prompt.renamePlaylist"), Title));
        if (name is null || name == Title)
            return;
        if (Edit(() => _library.Rename(Id, name), error => Strings.Format("toast.playlist.renameFailed", ("error", error))))
        {
            Title = name;
            _actions.Report(Strings.Get("toast.playlist.renamed"));
        }
    }

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private async Task DeleteAsync()
    {
        if (_actions.Confirm is null || !await _actions.Confirm(Strings.Format("confirm.deletePlaylist", ("title", Title))))
            return;
        if (Edit(() => _library.Delete(Id), error => Strings.Format("toast.playlist.deleteFailed", ("error", error))))
        {
            _actions.Report(Strings.Format("toast.playlist.deleted", ("title", Title)));
            _navigation?.Back();
        }
    }

    /// <summary>Runs <paramref name="change"/>; false, with a toast, when the
    /// playlist is gone or the change couldn't be saved.</summary>
    /// <param name="failed">The toast for a failed save, from its error.</param>
    private bool Edit(Func<bool> change, Func<string, string> failed)
    {
        try
        {
            if (change())
                return true;
            _actions.ReportError(Strings.Get("toast.track.localPlaylistGone"));
        }
        catch (LocalPlaylistsReadOnlyException)
        {
            _actions.ReportError(TrackActionsViewModel.ReadOnlyNote(_library));
        }
        catch (Exception ex)
        {
            _actions.ReportError(failed(ex.Message));
        }
        return false;
    }
}
