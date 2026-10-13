using Rambutan.Core.Models;

namespace Rambutan.Core.Stations;

/// <summary>The lists a listening service offers a user: a mix of what they play
/// and what's like it, recommendations, and their own library.</summary>
public enum StationKind { Mix, Recommended, Library }

/// <summary>
/// One track of a station as the service names it. <see cref="VideoId"/> is the
/// service's own YouTube link, when it has one: often a music video, so it plays
/// only when no YouTube Music song matches.
/// </summary>
public sealed record StationTrack(string Artist, string Title, TimeSpan? Duration = null, string? VideoId = null);

/// <summary>How a station track found something to play.</summary>
public enum StationMatch
{
    /// <summary>A YouTube Music song with the same title and artist.</summary>
    Song,
    /// <summary>No song matched; the service's own YouTube link plays.</summary>
    ServiceVideo,
    /// <summary>Nothing to play. The row stays, marked, rather than vanishing.</summary>
    NotFound,
}

/// <summary>A station track and what plays for it; <see cref="Track"/> is null
/// exactly when <see cref="Match"/> is <see cref="StationMatch.NotFound"/>.</summary>
public sealed record StationRow(StationTrack Source, StationMatch Match, TrackInfo? Track);

/// <summary>
/// The service answered, but not with a station: an unknown user, a profile
/// that shares nothing, or a response this client can't read (the endpoint is
/// undocumented and may change). Network failures stay
/// <see cref="HttpRequestException"/>, so the two read differently.
/// </summary>
public sealed class StationUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
