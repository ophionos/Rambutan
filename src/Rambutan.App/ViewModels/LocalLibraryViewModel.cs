using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Controls;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Core.Downloads;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Playlists;

namespace Rambutan.App.ViewModels;

/// <summary>One row in Library → On this computer: a watched folder's file, or
/// a download (<paramref name="Download"/>, with <paramref name="File"/> null),
/// whose <paramref name="Track"/> plays under its YouTube id.</summary>
public sealed record LocalTrackRow(LocalFile? File, TrackInfo Track, DownloadedTrack? Download = null)
    : IHasAccessibleName, ITrackRow
{
    // The name never changes: a changed file is a new row.
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public string Id => File?.Id ?? Track.VideoId;
    public string Title => Track.Title;

    public bool IsDownload => Download is not null;

    /// <summary>A download whose file is gone: listed dimmed, with Download again.</summary>
    public bool IsMissing => Download?.IsMissing == true;

    /// <summary>The download's size, "84 MB"; empty for a folder's file.</summary>
    public string Size => Download is { } d ? DownloadsViewModel.FormatSize(d.SizeBytes) : "";

    /// <summary>"Artist · Album", whichever the tags have; empty without either.</summary>
    public string Subtitle => string.Join(" · ", new[] { Track.ArtistDisplay, Track.Album }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>The dimming is visual only, so a missing file goes into the name.</summary>
    public string AccessibleName
    {
        get
        {
            var name = Subtitle.Length == 0
                ? Title
                : Strings.Format("queue.row.nameWithArtist", ("title", Title), ("artist", Subtitle));
            return IsMissing ? Strings.Format("downloads.row.missing", ("name", name)) : name;
        }
    }
}

/// <summary>A playlist on this computer, as the Playlists filter lists it.</summary>
public sealed record LocalPlaylistRow(LocalPlaylist Playlist) : IHasAccessibleName
{
    // A changed playlist is a new row.
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public string Name => Playlist.Name;

    /// <summary>"12 songs • 48 min"; "No songs yet" for an empty one.</summary>
    public string Summary => Playlist.Items.Count == 0
        ? Strings.Get("localPlaylist.noSongs")
        : Rambutan.App.Converters.DurationFormat.Summary(Playlist.Items.Select(i => i.Track).ToList());

    /// <summary>The first song's cover, if it has one.</summary>
    public TrackInfo? Cover => Playlist.Items.FirstOrDefault()?.Track;

    public string AccessibleName => Strings.Format("queue.row.nameWithArtist", ("title", Name), ("artist", Summary));
}

/// <summary>
/// Library → On this computer: the readable files in the watched folders and
/// the downloads, as the filter lists them (<see cref="LocalListing"/>), and
/// where the scan is. It fills while a scan reads, so the rows are edited in
/// place, never rebuilt: the scroll position and the focus stay. Missing and
/// unreadable files aren't listed; the status line counts them.
/// </summary>
public partial class LocalLibraryViewModel : ViewModelBase
{
    private readonly LocalLibraryScans _scans;
    private readonly PlaybackCoordinator? _coordinator;
    private readonly Func<IReadOnlyList<string>> _folders;
    private readonly Action<Action> _post;
    private bool _shown;
    private Task? _refresh;
    private bool _refreshAgain;

    public ObservableCollection<LocalTrackRow> Tracks { get; } = [];

    /// <summary>The downloads' jobs, export and actions; null where downloads
    /// aren't composed, and then only the folders' files are listed.</summary>
    public DownloadsViewModel? Downloads { get; }

    /// <summary>The playlists on this computer; null where they aren't composed.</summary>
    public LocalPlaylistLibrary? PlaylistLibrary { get; }

    public LocalLibraryScans Scans => _scans;

    /// <summary>The Playlists filter's rows, the one changed last first.</summary>
    public ObservableCollection<LocalPlaylistRow> Playlists { get; } = [];

    /// <summary>All and Folders, then Downloads and Playlists where they are composed.</summary>
    public IReadOnlyList<LocalSource> Filters { get; }

    /// <summary>All and Folders alone don't need a choice shown.</summary>
    public bool ShowsFilters => Filters.Count > 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloadsFilter), nameof(IsPlaylistsFilter), nameof(ListsFiles))]
    private LocalSource _filter;

    public bool IsDownloadsFilter => Filter == LocalSource.Downloads;

    public bool IsPlaylistsFilter => Filter == LocalSource.Playlists;

    /// <summary>The filter lists the watched folders' files, so the scan and Rescan belong to it.</summary>
    public bool ListsFiles => Filter is LocalSource.All or LocalSource.Folders;

    /// <summary>The Playlists filter has none to list.</summary>
    [ObservableProperty] private bool _noPlaylists;

    /// <summary>New playlist works: the playlists aren't read-only.</summary>
    [ObservableProperty] private bool _canCreatePlaylist;

    /// <summary>The filter lists the folders' files, and no folder is watched:
    /// it offers Settings. Under All, only while there is no download either.</summary>
    [ObservableProperty] private bool _needsFolders;

    /// <summary>The Downloads filter has nothing to list, and nothing downloads.</summary>
    [ObservableProperty] private bool _noDownloads;

    [ObservableProperty] private bool _hasFolders;
    [ObservableProperty] private bool _isScanning;

    /// <summary>The scan's progress while it runs, else how many files are listed.</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>Unreadable and missing files, and a failed scan; empty when there are none.</summary>
    [ObservableProperty] private string _problems = "";

    /// <summary>Folders are watched, no scan runs, and none of their files is music.</summary>
    [ObservableProperty] private bool _isEmpty;

    /// <param name="post">Runs on the UI thread; tests pass their own.</param>
    public LocalLibraryViewModel(
        LocalLibraryScans scans, PlaybackCoordinator? coordinator,
        Func<IReadOnlyList<string>> folders, Action<Action>? post = null, DownloadsViewModel? downloads = null,
        LocalPlaylistLibrary? playlists = null)
    {
        _scans = scans;
        _coordinator = coordinator;
        _folders = folders;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        Downloads = downloads;
        PlaylistLibrary = playlists;
        Filters =
        [
            LocalSource.All, LocalSource.Folders,
            .. downloads is null ? Array.Empty<LocalSource>() : [LocalSource.Downloads],
            .. playlists is null ? Array.Empty<LocalSource>() : [LocalSource.Playlists],
        ];
        // Raised on the thread that made the change.
        if (playlists is not null)
            playlists.Changed += () => _post(RefreshIfShown);
        // Only while shown: a library of thousands isn't sorted for no one.
        scans.Changed += () => _post(RefreshIfShown);
        if (downloads is not null)
        {
            // Already on the UI thread.
            downloads.DownloadedChanged += RefreshIfShown;
            downloads.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(DownloadsViewModel.HasJobs) or nameof(DownloadsViewModel.Summary))
                    UpdateStatus();
            };
        }
        Strings.WhenChanged(this, static vm => vm.UpdateStatus());
    }

    private void RefreshIfShown()
    {
        if (_shown)
            _ = RefreshAsync();
    }

    partial void OnFilterChanged(LocalSource value) => RefreshIfShown();

    /// <summary>The page is on screen: brings the list up to date and follows the scan.</summary>
    public Task ShowAsync()
    {
        _shown = true;
        return RefreshAsync();
    }

    public void Hide() => _shown = false;

    /// <summary>Searches the index off the UI thread (<see cref="LocalSearch"/>):
    /// at most <paramref name="limit"/> rows, best first, and the total.</summary>
    public Task<(IReadOnlyList<LocalTrackRow> Rows, int Total)> SearchAsync(
        string query, int limit, CancellationToken ct)
    {
        var index = _scans.Index;
        return Task.Run(() =>
        {
            var found = LocalSearch.Find(index.Files(), query, limit);
            IReadOnlyList<LocalTrackRow> rows = found.Files
                .Select(f => new LocalTrackRow(f, LocalTracks.ToTrack(f))).ToList();
            return (rows, found.Total);
        }, ct);
    }

    /// <summary>Lists the index again, off the UI thread, and edits the rows to
    /// match. A refresh asked for while one runs makes it run once more and
    /// returns it, so the task ends when the rows are up to date. UI thread only.</summary>
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
            var filter = Filter;
            var downloads = Downloads?.Downloaded ?? [];
            var rows = await Task.Run(() => LocalListing.For(filter, index.Files(), downloads)
                .Select(item => new LocalTrackRow(item.File, item.Track, item.Download))
                .ToList());
            Apply(Tracks, rows);
            ApplyPlaylists(filter == LocalSource.Playlists && PlaylistLibrary is { } library
                ? library.All.Select(p => new LocalPlaylistRow(p)).ToList()
                : []);
            UpdateStatus();
        } while (_refreshAgain);
    }

    private void UpdateStatus()
    {
        var status = _scans.Index.Status;
        HasFolders = _folders().Count > 0;
        IsScanning = status.IsScanning;
        var hasJobs = Downloads?.HasJobs == true;
        NeedsFolders = !HasFolders && Filter switch
        {
            LocalSource.Folders => true,
            LocalSource.All => Tracks.Count == 0 && !hasJobs,
            _ => false,
        };
        IsEmpty = HasFolders && ListsFiles && !IsScanning && Tracks.Count == 0;
        NoDownloads = IsDownloadsFilter && Tracks.Count == 0 && !hasJobs;
        NoPlaylists = IsPlaylistsFilter && Playlists.Count == 0;
        CanCreatePlaylist = PlaylistLibrary is { IsReadOnly: false };
        if (IsPlaylistsFilter)
        {
            Status = Playlists.Count == 0 ? "" : Strings.Format("library.local.statusPlaylists", ("count", Playlists.Count));
            Problems = PlaylistLibrary switch
            {
                { IsReadOnly: true } library => TrackActionsViewModel.ReadOnlyNote(library),
                // The list started again, empty: say where the old playlists went.
                { SetAside: { } path } => Strings.Format("localPlaylists.setAside", ("path", path)),
                _ => "",
            };
            return;
        }
        if (IsDownloadsFilter)
        {
            // The space the downloads use, as Settings says it.
            Status = Downloads?.Summary ?? "";
            Problems = "";
            return;
        }
        Status = status switch
        {
            { IsScanning: true, ToRead: 0 } => Strings.Get("settings.local.statusLooking"),
            { IsScanning: true } => Strings.Format("settings.local.statusReading", ("read", status.Read), ("total", status.ToRead)),
            _ when Filter == LocalSource.All => Strings.Format("library.local.statusSongs", ("count", Tracks.Count)),
            _ => Strings.Format("settings.local.statusFiles", ("count", Tracks.Count)),
        };
        var (_, unreadable, missing) = _scans.Index.Counts();
        var missingDownloads = Filter == LocalSource.All
            ? Downloads?.Downloaded.Count(d => d.IsMissing) ?? 0
            : 0;
        Problems = string.Join(" ", new[]
        {
            unreadable > 0 ? Strings.Format("settings.local.statusUnreadable", ("count", unreadable)) : "",
            missing > 0 ? Strings.Format("settings.local.statusMissing", ("count", missing)) : "",
            missingDownloads > 0 ? Strings.Format("library.local.statusMissingDownloads", ("count", missingDownloads)) : "",
            _scans.LastError is { } reason ? Strings.Format("settings.local.scanFailed", ("reason", reason)) : "",
        }.Where(s => s.Length > 0));
    }

    /// <summary>
    /// Edits <paramref name="rows"/> into <paramref name="wanted"/>: removes the
    /// rows that went, moves the ones whose place changed, replaces the ones whose
    /// file changed and inserts the new ones. A row is its file's id.
    /// </summary>
    internal static void Apply(ObservableCollection<LocalTrackRow> rows, IReadOnlyList<LocalTrackRow> wanted)
    {
        var wantedIds = wanted.Select(r => r.Id).ToHashSet();
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!wantedIds.Contains(rows[i].Id))
                rows.RemoveAt(i);
        }

        var present = rows.Select(r => r.Id).ToHashSet();
        for (var i = 0; i < wanted.Count; i++)
        {
            var row = wanted[i];
            if (i < rows.Count && rows[i].Id == row.Id)
            {
                if (Changed(rows[i], row))
                    rows[i] = row;
                continue;
            }
            if (present.Contains(row.Id))
            {
                var from = IndexOf(rows, row.Id, i + 1);
                rows.Move(from, i);
                if (Changed(rows[i], row))
                    rows[i] = row;
            }
            else
            {
                rows.Insert(i, row);
                present.Add(row.Id);
            }
        }
    }

    /// <summary>The playlists are few, but a rename shouldn't move the focus off
    /// the others: only the rows that changed are replaced.</summary>
    private void ApplyPlaylists(IReadOnlyList<LocalPlaylistRow> wanted)
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i == Playlists.Count)
                Playlists.Add(wanted[i]);
            else if (Playlists[i].Playlist != wanted[i].Playlist)
                Playlists[i] = wanted[i];
        }
        while (Playlists.Count > wanted.Count)
            Playlists.RemoveAt(Playlists.Count - 1);
    }

    // The index and the store keep an unchanged entry's instances, so a new
    // file or download record is a change; the track is made from them.
    private static bool Changed(LocalTrackRow shown, LocalTrackRow wanted) =>
        shown.File != wanted.File || shown.Download != wanted.Download;

    private static int IndexOf(ObservableCollection<LocalTrackRow> rows, string id, int start)
    {
        for (var i = start; i < rows.Count; i++)
        {
            if (rows[i].Id == id)
                return i;
        }
        throw new InvalidOperationException($"row {id} isn't in the list");
    }

    /// <summary>A row plays with the whole list as its queue, as a playlist's row
    /// does, leaving out the downloads whose file is gone; such a row downloads
    /// again instead.</summary>
    [RelayCommand]
    private Task PlayAsync(LocalTrackRow? row)
    {
        if (row is null)
            return Task.CompletedTask;
        if (row.IsMissing)
        {
            Downloads?.DownloadAgainCommand.Execute(row.Download);
            return Task.CompletedTask;
        }
        if (_coordinator is null)
            return Task.CompletedTask;
        var playable = Tracks.Where(r => !r.IsMissing).ToList();
        var index = playable.IndexOf(row);
        return index >= 0
            ? _coordinator.PlayQueueAsync(playable.Select(r => r.Track).ToList(), index)
            : Task.CompletedTask;
    }

    /// <summary>Plays every row the filter lists, from the first.</summary>
    [RelayCommand]
    private Task PlayAllAsync() =>
        Tracks.FirstOrDefault(r => !r.IsMissing) is { } first ? PlayAsync(first) : Task.CompletedTask;

    [RelayCommand]
    private void Rescan() => _scans.Start();
}
