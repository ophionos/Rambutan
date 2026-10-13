namespace Rambutan.Core.Downloads;

public enum CollectionDownloadKind
{
    /// <summary>Some tracks, or all, are neither downloaded nor on their way
    /// (failed jobs count here: they wait for a retry, not for time).</summary>
    NotDownloaded,
    /// <summary>At least one job is queued or running.</summary>
    Downloading,
    /// <summary>Every track has a download.</summary>
    Downloaded,
}

/// <param name="Fraction">0..1 over the whole collection: finished tracks count
/// whole, running ones by their progress.</param>
public sealed record CollectionDownloadState(
    CollectionDownloadKind Kind, int Total, int Downloaded, int Failed, double Fraction)
{
    public static readonly CollectionDownloadState Empty = new(CollectionDownloadKind.NotDownloaded, 0, 0, 0, 0);

    public static CollectionDownloadState Of(
        IEnumerable<string> videoIds, Func<string, bool> isDownloaded,
        IReadOnlyList<DownloadJob> jobs, bool complete = true) =>
        Of(videoIds, isDownloaded, jobs.Select(j => (j.Track.VideoId, j.State, j.Progress.Fraction)), complete);

    /// <param name="complete">False when the list is only part of the collection
    /// (a playlist not scrolled to its end): then "all downloaded" can't be claimed.</param>
    public static CollectionDownloadState Of(
        IEnumerable<string> videoIds, Func<string, bool> isDownloaded,
        IEnumerable<(string VideoId, DownloadJobState State, double? Fraction)> jobs, bool complete = true)
    {
        var ids = videoIds.Distinct().ToList();
        if (ids.Count == 0)
            return Empty;

        var jobOf = new Dictionary<string, (DownloadJobState State, double? Fraction)>();
        foreach (var job in jobs)
            jobOf.TryAdd(job.VideoId, (job.State, job.Fraction));

        int downloaded = 0, failed = 0, active = 0;
        var progress = 0.0;
        foreach (var id in ids)
        {
            if (isDownloaded(id))
            {
                downloaded++;
                progress += 1;
            }
            else if (jobOf.TryGetValue(id, out var job))
            {
                if (job.State == DownloadJobState.Failed)
                {
                    failed++;
                }
                else
                {
                    active++;
                    progress += job.Fraction ?? 0;
                }
            }
        }

        var kind = active > 0 ? CollectionDownloadKind.Downloading
            : downloaded == ids.Count && complete ? CollectionDownloadKind.Downloaded
            : CollectionDownloadKind.NotDownloaded;
        return new CollectionDownloadState(kind, ids.Count, downloaded, failed, progress / ids.Count);
    }
}
