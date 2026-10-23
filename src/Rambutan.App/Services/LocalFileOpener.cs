using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.LocalLibrary;

namespace Rambutan.App.Services;

/// <summary>
/// Plays or queues the files the user opened (Ctrl+O) or dropped on the window,
/// through the index (<see cref="LocalFileIndex.OpenAsync"/>), and says when
/// none of them could be played. The queue is never replaced. While files are
/// read, <see cref="Reading"/> says so, for the status strip.
/// </summary>
/// <param name="playAfterCurrent">Puts the tracks after the current one and plays the first.</param>
/// <param name="enqueue">Adds the tracks to the end of the queue.</param>
/// <param name="schedule">Runs a callback once after a delay; disposing cancels it.
/// Null uses a dispatcher timer.</param>
public sealed class LocalFileOpener(
    LocalFileIndex index, Func<IReadOnlyList<TrackInfo>, Task> playAfterCurrent,
    Action<IReadOnlyList<TrackInfo>> enqueue, ToastHostViewModel toasts,
    Func<TimeSpan, Action, IDisposable>? schedule = null)
{
    /// <summary>How long a read runs before it shows. Most opens are a few
    /// files read at once; showing them would only make the strip flicker, and
    /// the mini player's window change size.</summary>
    public static readonly TimeSpan ShowAfter = TimeSpan.FromMilliseconds(400);

    private readonly Func<TimeSpan, Action, IDisposable> _schedule =
        schedule ?? (static (delay, action) => DispatcherTimer.RunOnce(action, delay));
    private readonly Lock _lock = new();
    private int _running;
    private int _listed;
    private bool _shown;
    private IDisposable? _showing;

    /// <summary>
    /// The files being read, once a read has run for <see cref="ShowAfter"/>:
    /// how many were found, 0 while that isn't known yet (waiting for ffmpeg,
    /// listing a folder), summed over opens that overlap. Null when nothing is
    /// read, or not for long yet.
    /// </summary>
    public int? Reading
    {
        get
        {
            lock (_lock)
                return _shown ? _listed : null;
        }
    }

    /// <summary><see cref="Reading"/> changed. Raised on any thread.</summary>
    public event Action? ReadingChanged;

    /// <param name="playNow">True plays the files after the current track; false
    /// adds them to the end of the queue (a drop on the queue pane).</param>
    public async Task OpenAsync(IReadOnlyList<string> paths, bool playNow)
    {
        if (paths.Count == 0)
            return;
        IReadOnlyList<LocalFile> files;
        var listed = 0;
        Started();
        try
        {
            files = await index.OpenAsync(paths, count =>
            {
                listed = count;
                Listed(count);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            AppLog.Write("local", $"couldn't open files: {ex.GetType().Name}");
            files = [];
        }
        // The wait for ffmpeg failed (FfmpegManager's download): the files
        // weren't the problem, and the status strip offers the retry.
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TimeoutException or InvalidOperationException)
        {
            AppLog.Write("local", $"couldn't open files without ffmpeg: {ex.GetType().Name}");
            toasts.Show(Strings.Get("sidecar.failed"), ToastKind.Error);
            return;
        }
        finally
        {
            Ended(listed);
        }
        if (files.Count == 0)
        {
            toasts.Show(Strings.Get("toast.open.nothing"), ToastKind.Error);
            return;
        }

        var tracks = files.Select(f => LocalTracks.ToTrack(f)).ToList();
        if (playNow)
        {
            await playAfterCurrent(tracks);
            return;
        }
        enqueue(tracks);
        toasts.Show(tracks.Count == 1
            ? Strings.Format("toast.track.addedToQueue", ("title", tracks[0].Title))
            : Strings.Format("toast.open.addedToQueue", ("count", tracks.Count)));
    }

    private void Started()
    {
        lock (_lock)
        {
            if (_running++ > 0)
                return;
            _showing = _schedule(ShowAfter, Show);
        }
    }

    private void Show()
    {
        lock (_lock)
        {
            if (_running == 0 || _shown)
                return;
            _shown = true;
        }
        ReadingChanged?.Invoke();
    }

    private void Listed(int count)
    {
        bool shown;
        lock (_lock)
        {
            _listed += count;
            shown = _shown;
        }
        if (shown)
            ReadingChanged?.Invoke();
    }

    private void Ended(int listed)
    {
        bool shown;
        lock (_lock)
        {
            _listed -= listed;
            shown = _shown;
            if (--_running == 0)
            {
                _showing?.Dispose();
                _showing = null;
                _shown = false;
            }
        }
        if (shown)
            ReadingChanged?.Invoke();
    }
}
