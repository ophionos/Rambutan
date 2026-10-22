using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;
using Rambutan.LocalLibrary;

namespace Rambutan.App.Services;

/// <summary>
/// Starts local library scans from the app (at startup, after a folder is added,
/// on Rescan, and for a folder the watcher saw change) and keeps whether the
/// last one failed, so Settings can say so. A scan that a newer one stopped
/// isn't a failure. After each scan the watcher follows the watched folders
/// again, so one that failed or was unplugged is tried again.
/// </summary>
public sealed class LocalLibraryScans : IDisposable
{
    private readonly Func<IReadOnlyList<string>> _folders;

    /// <param name="watcher">Null where folders aren't watched (tests).</param>
    /// <param name="folders">The watched folders.</param>
    public LocalLibraryScans(
        LocalFileIndex index, LocalFolderWatcher? watcher = null, Func<IReadOnlyList<string>>? folders = null)
    {
        Index = index;
        Watcher = watcher;
        _folders = folders ?? (() => []);
        index.Changed += () => Changed?.Invoke();
        if (watcher is not null)
        {
            watcher.FoldersDue += roots => _ = Task.Run(() => RunAsync(roots));
            watcher.StateChanged += () => Changed?.Invoke();
        }
    }

    public LocalFileIndex Index { get; }

    public LocalFolderWatcher? Watcher { get; }

    /// <summary>Watched folders that aren't watched for changes now.</summary>
    public IReadOnlyList<string> NotWatched => Watcher?.NotWatched ?? [];

    /// <summary>The watched folders changed: the watcher follows them. Adding a
    /// folder also scans (<see cref="Start"/>), which does this after.</summary>
    public void FoldersChanged() => Watcher?.Sync(_folders());

    /// <summary>The index's <see cref="LocalFileIndex.Changed"/>, and
    /// a change of <see cref="LastError"/>. Raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Why the last scan failed; null when it didn't.</summary>
    public string? LastError { get; private set; }

    /// <summary>Starts a scan in the background; one already running stops for it.</summary>
    public void Start() => _ = Task.Run(() => RunAsync(null));

    /// <param name="roots">The folders to scan; null for every watched folder.</param>
    private async Task RunAsync(IReadOnlyList<string>? roots)
    {
        try
        {
            await Index.ScanAsync(roots, CancellationToken.None);
            LastError = null;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.Write("local", $"scan failed: {ex}");
        }
        FoldersChanged();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Watcher?.Dispose();
        Index.Dispose();
    }
}
