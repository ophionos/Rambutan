using Rambutan.Core.Models;

namespace Rambutan.Integrations.Scrobbling;

public static class ScrobbleEntries
{
    /// <summary>
    /// What a scrobbling service receives for a track. The first credited artist
    /// only: YouTube Music lists features as separate credits, and "A, B" would
    /// make the service invent an artist called that.
    /// </summary>
    public static ScrobbleEntry? From(TrackInfo track, DateTimeOffset startedAt, TimeSpan? duration = null)
    {
        var artist = track.Artists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Name))?.Name;
        if (artist is null || string.IsNullOrWhiteSpace(track.Title))
            return null;

        var length = duration ?? track.Duration;
        return new ScrobbleEntry
        {
            Artist = artist,
            Track = track.Title,
            Album = string.IsNullOrWhiteSpace(track.Album) ? null : track.Album,
            DurationSeconds = length is { } d && d > TimeSpan.Zero ? (int)Math.Round(d.TotalSeconds) : null,
            Timestamp = startedAt.ToUnixTimeSeconds(),
        };
    }
}
