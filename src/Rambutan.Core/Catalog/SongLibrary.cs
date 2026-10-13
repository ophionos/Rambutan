using Rambutan.Core.Models;

namespace Rambutan.Core.Catalog;

/// <summary>
/// The session's live view of which songs are in the library. A track's
/// <see cref="TrackInfo.Library"/> is a snapshot from the response that produced
/// it; this holds the user's presses since, so every surface shows one answer.
///
/// Like <see cref="TrackRatings"/>, a press applies before the request goes out
/// and is undone if the request fails, unless a newer press has superseded it.
/// </summary>
public sealed class SongLibrary(ICatalog catalog)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _known = new(StringComparer.Ordinal);
    private long _version;

    private readonly record struct Entry(bool InLibrary, long Version);

    /// <summary>A song's library state changed (videoId). Raised on the caller's
    /// thread, which for a failed request is wherever the request completed.</summary>
    public event Action<string>? Changed;

    public bool? Of(TrackInfo track)
    {
        lock (_lock)
        {
            return _known.TryGetValue(track.VideoId, out var entry)
                ? entry.InLibrary
                : track.Library?.InLibrary;
        }
    }

    /// <summary>
    /// Flips the song's library state, optimistically, and sends it. Returns the
    /// state now in effect. Throws <see cref="InvalidOperationException"/> for a
    /// track whose row carried no tokens; on a failed request the previous state
    /// is restored and the exception rethrown, so the caller can say what happened.
    /// </summary>
    public async Task<bool> ToggleAsync(TrackInfo track, CancellationToken ct = default)
    {
        if (track.Library is not { } toggle)
            throw new InvalidOperationException("This song's row carried no library tokens.");

        var videoId = track.VideoId;
        Entry? before;
        bool target;
        long version;
        lock (_lock)
        {
            before = _known.TryGetValue(videoId, out var existing) ? existing : null;
            target = !(before?.InLibrary ?? toggle.InLibrary);
            version = ++_version;
            _known[videoId] = new Entry(target, version);
        }
        Changed?.Invoke(videoId);

        try
        {
            await catalog.SetSongInLibraryAsync(toggle, target, ct);
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
}
