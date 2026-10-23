using Rambutan.Core.Playback;

namespace Rambutan.LocalLibrary;

/// <summary>
/// <see cref="IStreamSource"/> decorator that plays a track's download when it has
/// one, with no resolution and no network. The fault retry (bypassCache) skips the
/// download for that play and streams instead; it never deletes it, because a
/// download is something the user asked to keep.
/// </summary>
public sealed class DownloadsFirstSource(IStreamSource inner, DownloadStore downloads)
    : IStreamSource, IStreamWarmer, IOfflineAvailability
{
    public Task<ResolvedMedia> ResolveAsync(
        string videoId, bool bypassCache = false, CancellationToken ct = default) =>
        !bypassCache && downloads.TryGetMedia(videoId) is { } media
            ? Task.FromResult(media)
            : inner.ResolveAsync(videoId, bypassCache, ct);

    public bool IsWarm(string videoId) =>
        downloads.Contains(videoId) || inner is not IStreamWarmer warmer || warmer.IsWarm(videoId);

    public Task WarmAsync(string videoId, CancellationToken ct) =>
        inner is IStreamWarmer warmer && !downloads.Contains(videoId)
            ? warmer.WarmAsync(videoId, ct)
            : Task.CompletedTask;

    public bool IsAvailableOffline(string videoId) =>
        downloads.Contains(videoId) ||
        (inner is IOfflineAvailability offline && offline.IsAvailableOffline(videoId));
}
