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
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.App.ViewModels;

/// <summary>One file in Library → On this computer.</summary>
public sealed record LocalTrackRow(LocalFile File, TrackInfo Track) : IHasAccessibleName, ITrackRow
{
    // The name never changes: a changed file is a new row.
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public string Id => File.Id;
    public string Title => Track.Title;

    /// <summary>"Artist · Album", whichever the tags have; empty without either.</summary>
    public string Subtitle => string.Join(" · ", new[] { Track.ArtistDisplay, Track.Album }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    public string AccessibleName => Subtitle.Length == 0
        ? Title
        : Strings.Format("queue.row.nameWithArtist", ("title", Title), ("artist", Subtitle));
}

/// <summary>
/// Library → On this computer: the readable files in the watched folders, in
/// library order (<see cref="LocalTracks.InLibraryOrder"/>), and where the scan is.
/// It fills while a scan reads, so the rows are edited in place, never rebuilt:
/// the scroll position and the focus stay. Missing and unreadable files aren't
/// listed; the status line counts them.
/// </summary>
public partial class LocalLibraryViewModel : ViewModelBase
{
    private readonly LocalLibraryScans _scans;
    private readonly PlaybackCoordinator? _coordinator;
    private readonly Func<IReadOnlyList<string>> _folders;
    private readonly Action<Action> _post;
    private bool _shown;
    private bool _refreshing;
    private bool _refreshAgain;

    public ObservableCollection<LocalTrackRow> Tracks { get; } = [];

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
        Func<IReadOnlyList<string>> folders, Action<Action>? post = null)
    {
        _scans = scans;
        _coordinator = coordinator;
        _folders = folders;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        // Only while shown: a library of thousands isn't sorted for no one.
        scans.Changed += () => _post(() =>
        {
            if (_shown)
                _ = RefreshAsync();
        });
        Strings.WhenChanged(this, static vm => vm.UpdateStatus());
    }

    /// <summary>The page is on screen: brings the list up to date and follows the scan.</summary>
    public Task ShowAsync()
    {
        _shown = true;
        return RefreshAsync();
    }

    public void Hide() => _shown = false;

    /// <summary>Lists the index again, off the UI thread, and edits the rows to
    /// match. A refresh asked for while one runs runs once more after it.</summary>
    internal async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                var index = _scans.Index;
                var rows = await Task.Run(() => LocalTracks
                    .InLibraryOrder(index.Files().Where(f => f is { IsMissing: false, IsUnreadable: false }))
                    .Select(f => new LocalTrackRow(f, LocalTracks.ToTrack(f)))
                    .ToList());
                Apply(Tracks, rows);
                UpdateStatus();
            } while (_refreshAgain);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateStatus()
    {
        var status = _scans.Index.Status;
        HasFolders = _folders().Count > 0;
        IsScanning = status.IsScanning;
        IsEmpty = HasFolders && !IsScanning && Tracks.Count == 0;
        Status = status switch
        {
            { IsScanning: true, ToRead: 0 } => Strings.Get("settings.local.statusLooking"),
            { IsScanning: true } => Strings.Format("settings.local.statusReading", ("read", status.Read), ("total", status.ToRead)),
            _ => Strings.Format("settings.local.statusFiles", ("count", Tracks.Count)),
        };
        var (_, unreadable, missing) = _scans.Index.Counts();
        Problems = string.Join(" ", new[]
        {
            unreadable > 0 ? Strings.Format("settings.local.statusUnreadable", ("count", unreadable)) : "",
            missing > 0 ? Strings.Format("settings.local.statusMissing", ("count", missing)) : "",
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
                if (rows[i].File != row.File)
                    rows[i] = row;
                continue;
            }
            if (present.Contains(row.Id))
            {
                var from = IndexOf(rows, row.Id, i + 1);
                rows.Move(from, i);
                if (rows[i].File != row.File)
                    rows[i] = row;
            }
            else
            {
                rows.Insert(i, row);
                present.Add(row.Id);
            }
        }
    }

    private static int IndexOf(ObservableCollection<LocalTrackRow> rows, string id, int start)
    {
        for (var i = start; i < rows.Count; i++)
        {
            if (rows[i].Id == id)
                return i;
        }
        throw new InvalidOperationException($"row {id} isn't in the list");
    }

    /// <summary>A row plays with the whole list as its queue, as a playlist's row does.</summary>
    [RelayCommand]
    private Task PlayAsync(LocalTrackRow? row)
    {
        if (row is null || _coordinator is null)
            return Task.CompletedTask;
        var index = Tracks.IndexOf(row);
        return index >= 0
            ? _coordinator.PlayQueueAsync(Tracks.Select(r => r.Track).ToList(), index)
            : Task.CompletedTask;
    }

    [RelayCommand]
    private void Rescan() => _scans.Start();
}
