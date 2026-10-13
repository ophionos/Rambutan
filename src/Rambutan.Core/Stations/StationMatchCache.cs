using Rambutan.Core.Models;

namespace Rambutan.Core.Stations;

/// <summary>
/// What searching YouTube Music for one station track found: the same song,
/// and, when there was none, another version of it (a remix, a live take) to
/// play as a last resort. Both null when the search found neither.
/// </summary>
public sealed record StationSearchOutcome(TrackInfo? Song, TrackInfo? OtherVersion, DateTimeOffset SearchedAt);

/// <summary>
/// Search outcomes kept between loads, keyed by the track's normalized
/// artist and title, so a track that comes round again in a later sample isn't
/// searched again. An outcome with no song expires sooner than one with a song,
/// so a song YouTube Music adds later is still found. Failed searches are never
/// kept. Safe to use from several resolves at once.
/// </summary>
public sealed class StationMatchCache(TimeProvider? clock = null)
{
    public static readonly TimeSpan SongLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan NoSongLifetime = TimeSpan.FromDays(3);

    /// <summary>The newest outcomes kept when the cache is saved; a station
    /// samples a few hundred tracks at most, so this holds several months.</summary>
    public const int MaxEntries = 2000;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, StationSearchOutcome> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>The key a track is kept and de-duplicated under: two spellings
    /// the matcher can't tell apart are one track.</summary>
    public static string KeyOf(StationTrack track) =>
        TrackMatcher.Normalize(track.Artist) + "\u001f" + TrackMatcher.Normalize(track.Title);

    public bool TryGet(StationTrack track, out StationSearchOutcome outcome)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(KeyOf(track), out outcome!) && !IsExpired(outcome))
                return true;
        }
        outcome = null!;
        return false;
    }

    public void Put(StationTrack track, StationSearchOutcome outcome)
    {
        lock (_lock)
            _entries[KeyOf(track)] = outcome;
    }

    public IReadOnlyList<KeyValuePair<string, StationSearchOutcome>> Export()
    {
        lock (_lock)
            return _entries
                .Where(e => !IsExpired(e.Value))
                .OrderByDescending(e => e.Value.SearchedAt)
                .Take(MaxEntries)
                .ToList();
    }

    /// <summary>Adds saved outcomes; expired ones are skipped, and one already
    /// held from this run wins.</summary>
    public void Import(IEnumerable<KeyValuePair<string, StationSearchOutcome>> entries)
    {
        lock (_lock)
            foreach (var (key, outcome) in entries)
                if (!IsExpired(outcome))
                    _entries.TryAdd(key, outcome);
    }

    private bool IsExpired(StationSearchOutcome outcome)
    {
        var age = Now - outcome.SearchedAt;
        // A date in the future (a clock moved back) isn't trusted either.
        return age < TimeSpan.Zero || age > (outcome.Song is null ? NoSongLifetime : SongLifetime);
    }
}
