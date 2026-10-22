using System;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;
using Rambutan.LocalLibrary;

namespace Rambutan.App.Services;

/// <summary>
/// Starts local library scans from the app (at startup, after a folder is added,
/// on Rescan) and keeps whether the last one failed, so Settings can say so.
/// A scan that a newer one stopped isn't a failure.
/// </summary>
public sealed class LocalLibraryScans
{
    public LocalLibraryScans(LocalFileIndex index)
    {
        Index = index;
        index.Changed += () => Changed?.Invoke();
    }

    public LocalFileIndex Index { get; }

    /// <summary>The index's <see cref="LocalFileIndex.Changed"/>, and
    /// a change of <see cref="LastError"/>. Raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Why the last scan failed; null when it didn't.</summary>
    public string? LastError { get; private set; }

    /// <summary>Starts a scan in the background; one already running stops for it.</summary>
    public void Start() => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        try
        {
            await Index.ScanAsync(CancellationToken.None);
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
        Changed?.Invoke();
    }
}
