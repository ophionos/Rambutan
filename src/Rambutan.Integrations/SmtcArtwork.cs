using Rambutan.Core.Local;
using Rambutan.Core.Models;

namespace Rambutan.Integrations;

/// <summary>Where the media overlay's picture of a track comes from: a URL
/// Windows fetches itself, or a cover file on this computer.</summary>
public sealed record SmtcArtwork(Uri? Url, string? File)
{
    public static SmtcArtwork None { get; } = new(null, null);

    /// <summary>
    /// A local track's cover is its kept copy, found by id: it has no URL, as a
    /// path kept with the track would go stale when the profile's folder moves.
    /// Any other track's is its thumbnail URL.
    /// </summary>
    /// <param name="localCover">The kept cover's path for a local id, or null.</param>
    public static SmtcArtwork For(TrackInfo track, Func<string, string?>? localCover)
    {
        if (LocalIds.IsLocal(track.VideoId))
            return localCover?.Invoke(track.VideoId) is { Length: > 0 } path ? new(null, path) : None;
        return track.ThumbnailUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? new(uri, null)
            : None;
    }
}
