using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Core.Local;

namespace Rambutan.App.ViewModels;

/// <param name="IsUnavailable">The last scan couldn't list it (a drive that isn't
/// attached); its files keep the state they had.</param>
/// <param name="IsNotWatched">Its changes aren't noticed while the app runs.</param>
public sealed record WatchedFolderRow(string Path, bool IsUnavailable, bool IsNotWatched = false)
{
    public string RemoveName => Strings.Format("settings.local.removeName", ("folder", Path));
}

/// <summary>
/// Settings → Music on this computer: the watched folders, a rescan, and where
/// the scan is. Folders are per profile, like every setting.
/// </summary>
public partial class LocalFilesSettingsViewModel : ViewModelBase
{
    private readonly AppSettings _app;
    private readonly LocalLibraryScans _scans;
    private readonly Action _persist;
    private readonly LocalPaths _paths;

    [ObservableProperty] private bool _hasFolders;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _status = "";

    /// <summary>Unreadable and missing files; empty when there are none.</summary>
    [ObservableProperty] private string _problems = "";

    [ObservableProperty] private string? _error;

    public ObservableCollection<WatchedFolderRow> Folders { get; } = [];

    /// <summary>Set by the view: the platform folder picker, null when cancelled.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <param name="post">Runs a refresh on the UI thread; tests pass their own.</param>
    public LocalFilesSettingsViewModel(
        AppSettings app, LocalLibraryScans scans, Action persist, LocalPaths? paths = null, Action<Action>? post = null)
    {
        _app = app;
        _scans = scans;
        _persist = persist;
        _paths = paths ?? LocalPaths.Platform;
        post ??= action => Dispatcher.UIThread.Post(action);
        scans.Changed += () => post(Refresh);
        Refresh();
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (PickFolder is null || await PickFolder() is not { Length: > 0 } folder)
            return;
        AddFolder(folder);
    }

    internal void AddFolder(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var folders = _app.WatchedFolders ?? [];
        if (folders.Contains(full, _paths.Comparer))
            return;
        // A new list, never the old one changed: a scan may be reading it.
        _app.WatchedFolders = [.. folders, full];
        _persist();
        Refresh();
        _scans.Start();
    }

    [RelayCommand]
    private void RemoveFolder(WatchedFolderRow? row)
    {
        if (row is null)
            return;
        // Its files stay in the index, unlisted, and keep their ids if it comes back.
        _app.WatchedFolders = [.. (_app.WatchedFolders ?? []).Where(f => !_paths.Comparer.Equals(f, row.Path))];
        _persist();
        Refresh();
        _scans.FoldersChanged();
    }

    [RelayCommand]
    private void Rescan() => _scans.Start();

    internal void Refresh()
    {
        var status = _scans.Index.Status;
        var unavailable = status.UnavailableFolders.ToHashSet(_paths.Comparer);
        // A folder inside another is watched with it.
        var notWatched = _scans.NotWatched;
        bool NotWatched(string folder) => notWatched.Any(w => _paths.IsUnder(folder, w));
        var folders = _app.WatchedFolders ?? [];
        if (!folders.SequenceEqual(Folders.Select(f => f.Path))
            || Folders.Any(f => f.IsUnavailable != unavailable.Contains(f.Path) || f.IsNotWatched != NotWatched(f.Path)))
        {
            Folders.Clear();
            foreach (var folder in folders)
                Folders.Add(new WatchedFolderRow(folder, unavailable.Contains(folder), NotWatched(folder)));
        }
        HasFolders = folders.Count > 0;
        IsScanning = status.IsScanning;
        Error = _scans.LastError is { } reason ? Strings.Format("settings.local.scanFailed", ("reason", reason)) : null;

        var (readable, unreadable, missing) = _scans.Index.Counts();
        Status = status switch
        {
            { IsScanning: true, ToRead: 0 } => Strings.Get("settings.local.statusLooking"),
            { IsScanning: true } => Strings.Format("settings.local.statusReading", ("read", status.Read), ("total", status.ToRead)),
            _ => Strings.Format("settings.local.statusFiles", ("count", readable)),
        };
        Problems = string.Join(" ", new List<string>
        {
            unreadable > 0 ? Strings.Format("settings.local.statusUnreadable", ("count", unreadable)) : "",
            missing > 0 ? Strings.Format("settings.local.statusMissing", ("count", missing)) : "",
        }.Where(s => s.Length > 0));
    }
}
