using Rambutan.Core.Diagnostics;
using Rambutan.Core.Local;

namespace Rambutan.LocalLibrary;

/// <summary>
/// Watches the watched folders while the app runs and says which ones to scan
/// again, once each has been quiet for <see cref="LocalWatch.QuietPeriod"/>.
/// The rules are <see cref="LocalWatch"/>'s. A folder whose watcher can't run
/// is in <see cref="NotWatched"/> until a later <see cref="Sync"/> arms it;
/// the scans at startup and on Rescan still find its changes.
/// </summary>
public sealed class LocalFolderWatcher : IDisposable
{
    private readonly IReadOnlyList<string> _ignored;
    private readonly LocalPaths _paths;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers;
    private readonly HashSet<string> _notWatched;
    private readonly FolderChangeDebounce _debounce;
    private readonly ITimer _timer;
    private bool _disposed;

    /// <param name="ignored">The app's own folders: its writes there never count.</param>
    public LocalFolderWatcher(IReadOnlyList<string> ignored, LocalPaths? paths = null, TimeProvider? time = null)
    {
        _paths = paths ?? LocalPaths.Platform;
        _ignored = ignored.Select(Path.GetFullPath).ToList();
        _time = time ?? TimeProvider.System;
        _watchers = new(_paths.Comparer);
        _notWatched = new(_paths.Comparer);
        _debounce = new FolderChangeDebounce(LocalWatch.QuietPeriod, _paths.Comparer);
        _timer = _time.CreateTimer(_ => OnDue(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Folders to scan again. Raised on a background thread.</summary>
    public event Action<IReadOnlyList<string>>? FoldersDue;

    /// <summary><see cref="NotWatched"/> changed. Raised on any thread.</summary>
    public event Action? StateChanged;

    /// <summary>Watched folders whose watcher failed or couldn't start.</summary>
    public IReadOnlyList<string> NotWatched
    {
        get
        {
            lock (_lock)
                return _notWatched.ToList();
        }
    }

    /// <summary>
    /// Watches these folders and no others. A folder that isn't there now (an
    /// unplugged drive) isn't watched or marked: the scan says it is
    /// unavailable. One that failed before is tried again.
    /// </summary>
    public void Sync(IReadOnlyList<string> roots)
    {
        var wanted = LocalWatch.Outermost(roots, _paths);
        bool changed;
        lock (_lock)
        {
            if (_disposed)
                return;
            var before = _notWatched.Count;
            foreach (var root in _watchers.Keys.Where(r => !wanted.Contains(r, _paths.Comparer)).ToList())
                Stop(root);
            _notWatched.RemoveWhere(r => !wanted.Contains(r, _paths.Comparer));
            foreach (var root in wanted.Where(r => !_watchers.ContainsKey(r)))
                Start(root);
            changed = before != _notWatched.Count;
        }
        if (changed)
            StateChanged?.Invoke();
    }

    private void Start(string root)
    {
        if (!Directory.Exists(root))
        {
            _notWatched.Remove(root);
            return;
        }
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite | NotifyFilters.Size,
            // The most Windows allows; fewer events are lost to a full buffer.
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (_, e) => OnEvent(root, WatcherChange.Created, e.FullPath);
        watcher.Changed += (_, e) => OnEvent(root, WatcherChange.Changed, e.FullPath);
        watcher.Deleted += (_, e) => OnEvent(root, WatcherChange.DeletedOrRenamed, e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            OnEvent(root, WatcherChange.DeletedOrRenamed, e.OldFullPath);
            OnEvent(root, WatcherChange.DeletedOrRenamed, e.FullPath);
        };
        watcher.Error += (_, e) => OnError(root, watcher, e.GetException());
        try
        {
            // On Linux this is where the inotify limits show.
            watcher.EnableRaisingEvents = true;
            _watchers[root] = watcher;
            _notWatched.Remove(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            watcher.Dispose();
            _notWatched.Add(root);
            AppLog.Write("local", $"can't watch a folder: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Stop(string root)
    {
        if (_watchers.Remove(root, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private void OnEvent(string root, WatcherChange change, string path)
    {
        var isDirectory = change == WatcherChange.Created
            && !LocalScan.AudioExtensions.Contains(Path.GetExtension(path))
            && Directory.Exists(path);
        if (!LocalWatch.Counts(change, path, isDirectory, _ignored, _paths))
            return;
        Pending(root);
    }

    private void OnError(string root, FileSystemWatcher watcher, Exception error)
    {
        var action = LocalWatch.OnError(error);
        AppLog.Write("local", $"folder watcher error ({action}): {error.GetType().Name}: {error.Message}");
        if (action == WatchErrorAction.ScanAndStop)
        {
            lock (_lock)
            {
                if (_disposed || !_watchers.TryGetValue(root, out var current) || current != watcher)
                    return;
                Stop(root);
                _notWatched.Add(root);
            }
            StateChanged?.Invoke();
        }
        Pending(root);
    }

    private void Pending(string root)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _debounce.Changed(root, _time.GetUtcNow());
            Arm();
        }
    }

    private void OnDue()
    {
        IReadOnlyList<string> due;
        lock (_lock)
        {
            if (_disposed)
                return;
            due = _debounce.TakeDue(_time.GetUtcNow());
            Arm();
        }
        if (due.Count > 0)
            FoldersDue?.Invoke(due);
    }

    /// <summary>Sets the timer for the next folder due. Called under the lock.</summary>
    private void Arm()
    {
        if (_debounce.NextDue is { } next)
        {
            var wait = next - _time.GetUtcNow();
            _timer.Change(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var root in _watchers.Keys.ToList())
                Stop(root);
        }
        _timer.Dispose();
    }
}
