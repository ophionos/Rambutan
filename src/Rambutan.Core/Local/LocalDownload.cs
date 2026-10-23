using Rambutan.Core.Models;

namespace Rambutan.Core.Local;

/// <summary>
/// What the app knows about a file it downloaded, kept on the file's entry in
/// the local index.
/// </summary>
/// <param name="Track">The track as it was listed when it was downloaded.</param>
/// <param name="Quality">The quality tier it was fetched under, in the settings spelling.</param>
/// <param name="ArtworkPath">The cover's own copy beside the audio, so it shows
/// offline; null on downloads made before covers were kept, or whose cover
/// couldn't be fetched.</param>
/// <param name="RequestedAt">With <paramref name="Position"/>, its place in the
/// list (<see cref="Downloads.DownloadOrder"/>); null on downloads made before
/// the order was kept, which sort by when they finished.</param>
public sealed record LocalDownload(
    TrackInfo Track, string AudioCodec, string? Itag, string? Quality, double? LoudnessDb,
    long SizeBytes, DateTimeOffset DownloadedAt, string? ArtworkPath = null,
    DateTimeOffset? RequestedAt = null, int? Position = null);
