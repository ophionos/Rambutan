using Rambutan.Core.Models;
using Rambutan.Core.Settings;

namespace Rambutan.Core.Downloads;

/// <param name="Itag">Null when the source doesn't say (a copy from the cache).</param>
public sealed record FetchedAudio(string AudioCodec, string? Itag, AudioQuality Quality, double? LoudnessDb);

/// <summary>Writes one track's audio into a stream. Throws when it can't; a
/// cancelled or failed fetch is discarded by the caller.</summary>
public interface IAudioFetcher
{
    Task<FetchedAudio> FetchAsync(
        TrackInfo track, Stream target, IProgress<DownloadProgress> progress, CancellationToken ct);
}
