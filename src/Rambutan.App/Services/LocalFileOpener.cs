using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
/// none of them could be played. The queue is never replaced.
/// </summary>
/// <param name="playAfterCurrent">Puts the tracks after the current one and plays the first.</param>
/// <param name="enqueue">Adds the tracks to the end of the queue.</param>
public sealed class LocalFileOpener(
    LocalFileIndex index, Func<IReadOnlyList<TrackInfo>, Task> playAfterCurrent,
    Action<IReadOnlyList<TrackInfo>> enqueue, ToastHostViewModel toasts)
{
    /// <param name="playNow">True plays the files after the current track; false
    /// adds them to the end of the queue (a drop on the queue pane).</param>
    public async Task OpenAsync(IReadOnlyList<string> paths, bool playNow)
    {
        if (paths.Count == 0)
            return;
        IReadOnlyList<LocalFile> files;
        try
        {
            files = await index.OpenAsync(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            AppLog.Write("local", $"couldn't open files: {ex.GetType().Name}");
            files = [];
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
}
