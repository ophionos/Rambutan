using Rambutan.Core.Models;

namespace Rambutan.Lyrics.Providers;

/// <param name="VideoId">The track's identity: the cache and custom-store key. Providers match on the rest.</param>
/// <param name="Duration">The track length, used to reject lyrics timed for a different cut.</param>
public sealed record LyricsQuery(
    string VideoId,
    string Title,
    string Artist,
    string? Album = null,
    TimeSpan? Duration = null)
{
    /// <summary>
    /// Builds a query from a catalog track. Uses the first credited artist only:
    /// YouTube Music lists collaborators separately, and lyrics databases disagree
    /// on how to join them ("A & B", "A, B", "A feat. B"), while the primary
    /// artist is what they all agree on.
    /// </summary>
    public static LyricsQuery FromTrack(TrackInfo track) => new(
        track.VideoId,
        track.Title,
        track.Artists.Count > 0 ? track.Artists[0].Name : "",
        string.IsNullOrWhiteSpace(track.Album) ? null : track.Album,
        track.Duration);
}
