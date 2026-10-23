using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;

namespace Rambutan.Core.Downloads;

public enum DownloadJobState
{
    Queued,
    Downloading,
    Failed,
}

/// <summary>
/// One track on its way to becoming a download. A job that succeeds leaves the
/// queue and becomes a <see cref="DownloadedTrack"/>; one the user cancels leaves
/// it with nothing kept; one that fails stays, with its reason, until it is retried
/// or cancelled.
/// </summary>
public sealed class DownloadJob
{
    internal DownloadJob(TrackInfo track, DownloadOrder order)
    {
        Track = track;
        Order = order;
    }

    public TrackInfo Track { get; }

    /// <summary>Where the finished download will sit in the list; a retry keeps it.</summary>
    public DownloadOrder Order { get; }
    public DownloadJobState State { get; internal set; }
    public DownloadProgress Progress { get; internal set; }

    /// <summary>Why the last attempt failed, for the log and a tooltip; null
    /// unless <see cref="State"/> is <see cref="DownloadJobState.Failed"/>.</summary>
    public string? Error { get; internal set; }

    internal CancellationTokenSource? Cts { get; set; }
}

/// <summary>
/// Runs download jobs, at most <see cref="MaxParallel"/> at once, in the order they
/// were asked for. Each job resolves its stream only when it starts: googlevideo
/// URLs expire and are bound to the address that asked, so a URL resolved for the
/// 200th track of a playlist at enqueue time would be dead by its turn.
/// <para>
/// Jobs survive a restart: the queue saves what is still waiting (failed jobs
/// included) after every change, and <see cref="Restore"/> puts it back.
/// </para>
/// Events arrive on whichever thread made the change or ran the job; subscribers
/// marshal. They are never raised under the queue's lock, so a subscriber that
/// waits for another thread reading the queue can't deadlock.
/// </summary>
public sealed class DownloadQueue(IDownloadStore store, int maxParallel = 2)
{
    private readonly object _lock = new();
    private readonly List<DownloadJob> _jobs = [];

    public int MaxParallel { get; } = Math.Max(1, maxParallel);

    public event Action? JobsChanged;

    public event Action<DownloadJob>? ProgressChanged;

    /// <summary>The finished downloads changed: one completed or was deleted
    /// (the videoId).</summary>
    public event Action<string>? DownloadsChanged;

    public IReadOnlyList<DownloadJob> Jobs
    {
        get { lock (_lock) return _jobs.ToArray(); }
    }

    public IReadOnlyList<DownloadedTrack> Downloaded => store.List();

    public bool IsDownloaded(string videoId) => store.Contains(videoId);

    public bool IsQueued(string videoId)
    {
        lock (_lock)
            return _jobs.Any(j => j.Track.VideoId == videoId);
    }

    /// <summary>Puts back the jobs a previous run left unfinished. Call once, at start.</summary>
    public void Restore() => Add(store.LoadPending());

    /// <summary>Queues the tracks that are neither downloaded nor already queued,
    /// as one request in their given order; returns how many that was.</summary>
    public int Enqueue(IEnumerable<TrackInfo> tracks)
    {
        var requestedAt = DateTimeOffset.UtcNow;
        return Add(tracks.Select((track, i) => new PendingDownload(track, new DownloadOrder(requestedAt, i))));
    }

    private int Add(IEnumerable<PendingDownload> requests)
    {
        var added = 0;
        lock (_lock)
        {
            foreach (var (track, order) in requests)
            {
                if (_jobs.Any(j => j.Track.VideoId == track.VideoId) || store.Contains(track.VideoId))
                    continue;
                _jobs.Add(new DownloadJob(track, order));
                added++;
            }
        }

        if (added > 0)
            Changed();
        return added;
    }

    /// <summary>Stops the track's job, wherever it is, and drops it. Nothing of a
    /// partial download is kept.</summary>
    public void Cancel(string videoId)
    {
        DownloadJob? job;
        lock (_lock)
        {
            job = _jobs.FirstOrDefault(j => j.Track.VideoId == videoId);
            if (job is null)
                return;
            _jobs.Remove(job);
        }

        job.Cts?.Cancel();
        Changed();
    }

    public void CancelAll()
    {
        DownloadJob[] jobs;
        lock (_lock)
        {
            jobs = _jobs.ToArray();
            _jobs.Clear();
        }

        if (jobs.Length == 0)
            return;
        foreach (var job in jobs)
            job.Cts?.Cancel();
        Changed();
    }

    public void Retry(string videoId)
    {
        lock (_lock)
        {
            if (_jobs.FirstOrDefault(j => j.Track.VideoId == videoId) is not { State: DownloadJobState.Failed } job)
                return;
            _jobs.Remove(job);
            _jobs.Add(job);
            Reset(job);
        }

        Changed();
    }

    public void RetryAllFailed()
    {
        lock (_lock)
        {
            var failed = _jobs.Where(j => j.State is DownloadJobState.Failed).ToList();
            if (failed.Count == 0)
                return;
            foreach (var job in failed)
            {
                _jobs.Remove(job);
                _jobs.Add(job);
                Reset(job);
            }
        }

        Changed();
    }

    private static void Reset(DownloadJob job)
    {
        job.State = DownloadJobState.Queued;
        job.Error = null;
        job.Progress = default;
    }

    public void Delete(string videoId) => Delete([videoId]);

    /// <summary>Stops the tracks' jobs and deletes their downloads together: the
    /// store saves its records once, not once per track.</summary>
    public void Delete(IReadOnlyCollection<string> videoIds)
    {
        if (videoIds.Count == 0)
            return;
        var ids = videoIds.ToHashSet(StringComparer.Ordinal);
        List<DownloadJob> stopped;
        lock (_lock)
        {
            stopped = _jobs.Where(j => ids.Contains(j.Track.VideoId)).ToList();
            foreach (var job in stopped)
                _jobs.Remove(job);
        }
        foreach (var job in stopped)
            job.Cts?.Cancel();
        if (stopped.Count > 0)
            Changed();

        store.Delete(ids);
        foreach (var id in ids)
            DownloadsChanged?.Invoke(id);
    }

    private void Changed()
    {
        List<DownloadJob> starting = [];
        PendingDownload[] pending;
        lock (_lock)
        {
            var running = _jobs.Count(j => j.State is DownloadJobState.Downloading);
            foreach (var job in _jobs)
            {
                if (running >= MaxParallel)
                    break;
                if (job.State is not DownloadJobState.Queued)
                    continue;
                job.State = DownloadJobState.Downloading;
                job.Cts = new CancellationTokenSource();
                starting.Add(job);
                running++;
            }

            pending = _jobs.Select(j => new PendingDownload(j.Track, j.Order)).ToArray();
        }

        try
        {
            store.SavePending(pending);
        }
        catch (Exception ex)
        {
            // The jobs still run; only their survival across a restart is lost.
            AppLog.Write("downloads", $"couldn't save pending downloads: {ex.Message}");
        }

        JobsChanged?.Invoke();
        foreach (var job in starting)
            _ = RunAsync(job, job.Cts!.Token);
    }

    private async Task RunAsync(DownloadJob job, CancellationToken ct)
    {
        string? error = null;
        try
        {
            var progress = new Reporter(p =>
            {
                job.Progress = p;
                ProgressChanged?.Invoke(job);
            });
            await Task.Yield(); // never run a store's synchronous prefix on the caller's thread
            await store.DownloadAsync(job.Track, job.Order, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // Cancel already removed the job and told everyone.
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Write("downloads", $"download of {job.Track.VideoId} failed: {ex.Message}");
        }

        bool cancelled;
        lock (_lock)
        {
            // Cancelled as it finished: the cancel already removed the job and told everyone.
            cancelled = !_jobs.Contains(job);
            if (!cancelled)
            {
                if (error is null)
                {
                    _jobs.Remove(job);
                }
                else
                {
                    job.State = DownloadJobState.Failed;
                    job.Error = error;
                }
                job.Cts?.Dispose();
                job.Cts = null;
            }
        }

        // Even a cancelled download may have committed.
        if (error is null)
            DownloadsChanged?.Invoke(job.Track.VideoId);
        if (!cancelled)
            Changed();
    }

    /// <summary>Reports on the reporting thread; <see cref="Progress{T}"/> would
    /// post to whatever context the queue was built on.</summary>
    private sealed class Reporter(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
