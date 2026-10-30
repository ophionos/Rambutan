using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Stations;

namespace Rambutan.Core.Import;

public enum ImportMatchState
{
    /// <summary>YouTube Music has the same recording.</summary>
    Found,

    /// <summary>The search found no song that is the same recording.</summary>
    NotFound,

    /// <summary>The search couldn't be made (offline, refused); nothing is
    /// known, and a later search can still find it.</summary>
    Failed,
}

/// <summary>What matching found for one imported song.</summary>
/// <param name="Song">The song, when <see cref="State"/> is <see cref="ImportMatchState.Found"/>.</param>
public sealed record ImportMatch(ImportMatchState State, TrackInfo? Song = null)
{
    public static readonly ImportMatch NotFound = new(ImportMatchState.NotFound);
    public static readonly ImportMatch Failed = new(ImportMatchState.Failed);
}

/// <summary>How far matching is: <see cref="Done"/> of <see cref="Total"/>
/// distinct songs.</summary>
public readonly record struct ImportProgress(int Done, int Total);

/// <summary>
/// Matches imported songs to YouTube Music songs: one search per distinct
/// song ("artist title"), picked by <see cref="TrackMatcher"/> without its last
/// resort, so another version (a remix, a live take) never matches.
/// <paramref name="searchSongs"/> should be a signed-out catalog's, as the
/// stations' is: an import is thousands of searches the user never typed, and
/// they don't belong in the account's search history. Searches go one at a
/// time, <paramref name="spacing"/> apart, so a large import doesn't look like
/// a flood. With <paramref name="cache"/>, a song searched before (by an
/// earlier import) isn't searched again while its answer is current.
/// </summary>
public sealed class ImportMatcher(
    Func<string, CancellationToken, Task<IReadOnlyList<TrackInfo>>> searchSongs,
    StationMatchCache cache,
    TimeSpan? spacing = null,
    TimeProvider? clock = null)
{
    public static readonly TimeSpan DefaultSpacing = TimeSpan.FromMilliseconds(250);

    /// <summary>Searches that fail in a row before the rest are left
    /// unsearched: YouTube Music is out of reach, and thousands more requests
    /// wouldn't change that.</summary>
    public const int GiveUpAfter = 5;

    private readonly TimeSpan _spacing = spacing ?? DefaultSpacing;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The key an imported song is matched, cached and de-duplicated
    /// under: its first artist and its title, as the stations' are.</summary>
    public static string KeyOf(ImportedTrack track) => StationMatchCache.KeyOf(Query(track));

    public static StationTrack Query(ImportedTrack track) =>
        new(track.Artists.Count > 0 ? track.Artists[0] : "", track.Title, track.Duration);

    /// <summary>
    /// A match for each distinct song of <paramref name="tracks"/>, by
    /// <see cref="KeyOf"/>. A failed search is <see cref="ImportMatch.Failed"/>
    /// and isn't kept; after <see cref="GiveUpAfter"/> failures in a row the
    /// songs not yet searched are failed too. Throws only when cancelled.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ImportMatch>> MatchAsync(
        IEnumerable<ImportedTrack> tracks, IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var distinct = new Dictionary<string, ImportedTrack>(StringComparer.Ordinal);
        foreach (var track in tracks)
            distinct.TryAdd(KeyOf(track), track);

        var matches = new Dictionary<string, ImportMatch>(StringComparer.Ordinal);
        var unknown = new List<(string Key, StationTrack Query)>();
        foreach (var (key, track) in distinct)
        {
            var query = Query(track);
            if (cache.TryGet(query, out var known))
                matches[key] = known.Song is { } song ? new ImportMatch(ImportMatchState.Found, song) : ImportMatch.NotFound;
            else
                unknown.Add((key, query));
        }
        progress?.Report(new ImportProgress(matches.Count, distinct.Count));

        var failedInARow = 0;
        var searched = 0;
        foreach (var (key, query) in unknown)
        {
            ct.ThrowIfCancellationRequested();
            if (failedInARow >= GiveUpAfter)
            {
                matches[key] = ImportMatch.Failed;
                continue;
            }
            if (searched++ > 0 && _spacing > TimeSpan.Zero)
                await Task.Delay(_spacing, _clock, ct).ConfigureAwait(false);
            try
            {
                var text = query.Artist.Length > 0 ? $"{query.Artist} {query.Title}" : query.Title;
                var results = await searchSongs(text, ct).ConfigureAwait(false);
                var candidates = results.Count > StationResolver.CandidateCount
                    ? results.Take(StationResolver.CandidateCount).ToList()
                    : results;
                var song = TrackMatcher.Pick(query, candidates);
                cache.Put(query, new StationSearchOutcome(song, null, cache.Now));
                matches[key] = song is null ? ImportMatch.NotFound : new ImportMatch(ImportMatchState.Found, song);
                failedInARow = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Not kept: an outage isn't an answer.
                matches[key] = ImportMatch.Failed;
                if (++failedInARow == GiveUpAfter)
                    AppLog.Write("import", $"{GiveUpAfter} searches failed in a row, the last with {ex.GetType().Name}; the rest are left unsearched.");
            }
            progress?.Report(new ImportProgress(matches.Count, distinct.Count));
        }
        progress?.Report(new ImportProgress(distinct.Count, distinct.Count));
        return matches;
    }

    /// <summary>
    /// Keeps the user's own pick for a song, so the next import of it takes
    /// that pick rather than searching again.
    /// </summary>
    public void Remember(ImportedTrack track, TrackInfo song) =>
        cache.Put(Query(track), new StationSearchOutcome(song, null, cache.Now));
}
