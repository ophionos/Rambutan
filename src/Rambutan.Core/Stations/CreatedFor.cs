namespace Rambutan.Core.Stations;

/// <summary>The playlists ListenBrainz makes for a user from their listens.</summary>
public enum CreatedForKind { DailyJams, WeeklyJams, WeeklyExploration }

/// <summary>
/// One playlist ListenBrainz made for the user, as its list names it.
/// <see cref="ExpiresAt"/> is null when the answer gives no date.
/// </summary>
public sealed record CreatedForPlaylist(
    CreatedForKind Kind, string Id, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

/// <summary>Which of the user's playlists Home shows.</summary>
public static class CreatedFor
{
    /// <summary>The order Home shows them in.</summary>
    public static IReadOnlyList<CreatedForKind> Order { get; } =
        [CreatedForKind.DailyJams, CreatedForKind.WeeklyJams, CreatedForKind.WeeklyExploration];

    /// <summary>
    /// The newest playlist of each kind that hasn't expired at
    /// <paramref name="now"/>, in <see cref="Order"/>. ListenBrainz keeps old
    /// weeks in the list after it stops making new ones, so a kind whose newest
    /// playlist has expired has none. One without an expiry date counts as
    /// current.
    /// </summary>
    public static IReadOnlyList<CreatedForPlaylist> Current(IEnumerable<CreatedForPlaylist> listed, DateTimeOffset now)
    {
        var newest = listed
            .GroupBy(p => p.Kind)
            .Select(g => g.MaxBy(p => p.CreatedAt)!)
            .Where(p => p.ExpiresAt is not { } expires || expires > now)
            .ToDictionary(p => p.Kind);
        return Order.Where(newest.ContainsKey).Select(k => newest[k]).ToList();
    }
}

/// <summary>A listening service that makes playlists for a user.</summary>
public interface ICreatedForSource
{
    /// <summary>
    /// The playlists the service made for <paramref name="user"/>, of the
    /// kinds this app knows, in any order; others are skipped. Throws
    /// <see cref="StationUnavailableException"/> when the service doesn't know
    /// the user, and <see cref="HttpRequestException"/> when it can't be reached.
    /// </summary>
    Task<IReadOnlyList<CreatedForPlaylist>> GetCreatedForAsync(string user, CancellationToken ct = default);

    /// <summary>
    /// The playlist's tracks, in order. Throws
    /// <see cref="StationUnavailableException"/> when the playlist is gone, and
    /// <see cref="HttpRequestException"/> when the service can't be reached.
    /// </summary>
    Task<IReadOnlyList<StationTrack>> GetPlaylistAsync(string id, CancellationToken ct = default);
}
