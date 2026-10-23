using Rambutan.Core.Models;

namespace Rambutan.Core.Downloads;

public readonly record struct DownloadProgress(long Bytes, long? Total)
{
    public double? Fraction => Total is > 0 and var total ? Math.Clamp((double)Bytes / total, 0, 1) : null;
}

/// <summary>Where a download sits in the list: requests newest first, and the
/// tracks of one request (an album, a playlist) in the order they were asked for,
/// however the parallel jobs happen to finish.</summary>
public readonly record struct DownloadOrder(DateTimeOffset RequestedAt, int Position);

/// <summary>A job not yet finished, as saved across a restart.</summary>
public sealed record PendingDownload(TrackInfo Track, DownloadOrder Order);

/// <param name="IsMissing">The record exists but its file doesn't (deleted by
/// hand, or on a drive that isn't attached). Shown as such, never played.</param>
public sealed record DownloadedTrack(
    TrackInfo Track, long SizeBytes, DateTimeOffset DownloadedAt, bool IsMissing = false);

/// <summary>
/// Where downloads live: the files, their records, and the jobs still waiting.
/// <see cref="DownloadQueue"/> decides what runs when; the store only fetches,
/// keeps and forgets.
/// </summary>
public interface IDownloadStore
{
    /// <summary>The finished downloads in <see cref="DownloadOrder"/>.</summary>
    IReadOnlyList<DownloadedTrack> List();

    /// <summary>True when the track has a finished download whose file exists.</summary>
    bool Contains(string videoId);

    /// <summary>Fetches the track and records it once complete. A cancelled or
    /// failed download leaves nothing behind.</summary>
    Task DownloadAsync(
        TrackInfo track, DownloadOrder order, IProgress<DownloadProgress> progress, CancellationToken ct);

    /// <summary>Deletes the downloads' files and records, saving the records once
    /// for all of them. An id with no download is skipped.</summary>
    void Delete(IReadOnlyCollection<string> videoIds);

    /// <summary>The jobs saved by <see cref="SavePending"/>, so downloads the
    /// app closed on resume at the next start instead of vanishing.</summary>
    IReadOnlyList<PendingDownload> LoadPending();

    void SavePending(IReadOnlyList<PendingDownload> pending);
}
