using Rambutan.Core.Models;

namespace Rambutan.Core.Catalog;

/// <summary>
/// The session's live view of the user's ratings. A track's
/// <see cref="TrackInfo.Rating"/> is a snapshot from whichever response produced
/// it, and the same song reaches the player bar, the queue and several pages as
/// separate records. This holds what changed since those snapshots (the user's own
/// presses, and lookups for tracks whose source didn't say), so every surface
/// shows one answer.
///
/// Rating is a toggle: pressing the rating a track already has clears it. The
/// change is applied before the request goes out and undone if it fails, so the
/// control never waits on the network and never keeps a state the server refused.
/// </summary>
public sealed class TrackRatings(ICatalog catalog)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _known = new(StringComparer.Ordinal);
    private long _version;

    private readonly record struct Entry(Rating Rating, long Version);

    /// <summary>A track's rating changed (videoId). Raised on the caller's thread,
    /// which for a failed request is wherever the request completed.</summary>
    public event Action<string>? Changed;

    /// <summary>This session's value for a track, or null if nothing has changed
    /// or been looked up since the track's source loaded.</summary>
    public Rating? Get(string videoId)
    {
        lock (_lock)
            return _known.TryGetValue(videoId, out var entry) ? entry.Rating : null;
    }

    public Rating? Of(TrackInfo track) => Get(track.VideoId) ?? track.Rating;

    public static Rating Toggle(Rating current, Rating pressed) =>
        current == pressed ? Rating.None : pressed;

    /// <summary>
    /// Applies <paramref name="pressed"/> as a toggle, optimistically, and sends it.
    /// Returns the rating now in effect. On failure the previous state is restored
    /// (unless a newer press has superseded this one) and the exception rethrown,
    /// so the caller can say what happened.
    /// </summary>
    public async Task<Rating> ToggleAsync(
        TrackInfo track, Rating pressed, CancellationToken ct = default)
    {
        var videoId = track.VideoId;
        Entry? before;
        Rating target;
        long version;
        lock (_lock)
        {
            before = _known.TryGetValue(videoId, out var existing) ? existing : null;
            target = Toggle(before?.Rating ?? track.Rating ?? Rating.None, pressed);
            version = ++_version;
            _known[videoId] = new Entry(target, version);
        }
        Changed?.Invoke(videoId);

        try
        {
            await catalog.RateAsync(videoId, target, ct);
            return target;
        }
        catch
        {
            var reverted = false;
            lock (_lock)
            {
                if (_known.TryGetValue(videoId, out var current) && current.Version == version)
                {
                    if (before is { } previous)
                        _known[videoId] = previous;
                    else
                        _known.Remove(videoId);
                    reverted = true;
                }
            }
            if (reverted)
                Changed?.Invoke(videoId);
            throw;
        }
    }

    /// <summary>
    /// Fills in the rating of a track whose source didn't report one, by asking
    /// the catalog. Returns the known rating (null if the catalog can't say, e.g.
    /// signed out). A press made while the lookup was in flight wins over it.
    /// </summary>
    public async Task<Rating?> LookUpAsync(TrackInfo track, CancellationToken ct = default)
    {
        if (Of(track) is { } known)
            return known;

        var fetched = await catalog.GetRatingAsync(track.VideoId, ct);
        if (fetched is not { } rating)
            return null;

        lock (_lock)
        {
            if (_known.TryGetValue(track.VideoId, out var pressed))
                return pressed.Rating;
            _known[track.VideoId] = new Entry(rating, ++_version);
        }
        Changed?.Invoke(track.VideoId);
        return rating;
    }
}
