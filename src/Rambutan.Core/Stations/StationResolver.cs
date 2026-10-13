using Rambutan.Core.Models;

namespace Rambutan.Core.Stations;

/// <summary>
/// Turns a station into rows that play: each track searched on YouTube Music
/// as "artist title" and matched by <see cref="TrackMatcher"/>; failing that the
/// station's own YouTube link; failing that a row marked not found.
///
/// <paramref name="searchSongs"/> is a song search, <c>ICatalog.SearchSongsAsync</c>
/// in the app. It should be a signed-out catalog's: a station is dozens of
/// searches the user never typed, and they don't belong in the account's
/// search history. With a <paramref name="cache"/>, a track searched before
/// isn't searched again while its outcome is current.
/// </summary>
public sealed class StationResolver(
    Func<string, CancellationToken, Task<IReadOnlyList<TrackInfo>>> searchSongs, int maxConcurrentSearches = 3,
    StationMatchCache? cache = null)
{
    // Shared by every resolve, so two stations loading together still send at
    // most this many searches at once.
    private readonly SemaphoreSlim _gate = new(Math.Max(1, maxConcurrentSearches));

    /// <summary>How many search results are looked at; the right song, when
    /// YouTube Music has it, is almost always among the first few.</summary>
    public const int CandidateCount = 5;

    /// <summary>
    /// One row per distinct track (a station can name a track twice), in the
    /// station's order. A failed search falls back like a search without a match;
    /// when every search that ran failed, the first failure is thrown, since the
    /// station would otherwise look found while YouTube Music was simply
    /// unreachable. Tracks answered from the cache don't count either way.
    /// </summary>
    public async Task<IReadOnlyList<StationRow>> ResolveAsync(
        IReadOnlyList<StationTrack> tracks, CancellationToken ct = default)
    {
        var distinct = Distinct(tracks);
        var rows = new StationRow[distinct.Count];
        var failures = new Exception?[distinct.Count];
        var searched = new bool[distinct.Count];

        await Task.WhenAll(distinct.Select(async (track, i) =>
        {
            if (cache is not null && cache.TryGet(track, out var known))
            {
                rows[i] = Resolve(track, known);
                return;
            }

            await _gate.WaitAsync(ct);
            try
            {
                searched[i] = true;
                StationSearchOutcome outcome;
                try
                {
                    var results = await searchSongs($"{track.Artist} {track.Title}", ct);
                    outcome = Outcome(track, results, cache?.Now ?? DateTimeOffset.UtcNow);
                    cache?.Put(track, outcome);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Not kept: an outage isn't an answer.
                    failures[i] = ex;
                    outcome = new StationSearchOutcome(null, null, default);
                }
                rows[i] = Resolve(track, outcome);
            }
            finally
            {
                _gate.Release();
            }
        }));

        var ran = Enumerable.Range(0, distinct.Count).Where(i => searched[i]).ToList();
        if (ran.Count > 0 && ran.All(i => failures[i] is not null))
            throw failures[ran[0]]!;
        return rows;
    }

    public static StationRow Resolve(StationTrack track, IReadOnlyList<TrackInfo> results) =>
        Resolve(track, Outcome(track, results, default));

    public static StationSearchOutcome Outcome(StationTrack track, IReadOnlyList<TrackInfo> results, DateTimeOffset searchedAt)
    {
        var candidates = results.Count > CandidateCount ? results.Take(CandidateCount).ToList() : results;
        var song = TrackMatcher.Pick(track, candidates);
        return new StationSearchOutcome(song, song is null ? TrackMatcher.Pick(track, candidates, lastResort: true) : null, searchedAt);
    }

    /// <summary>The row for one track, given what a search found for it. Another
    /// version plays only when the station gave no link of its own.</summary>
    public static StationRow Resolve(StationTrack track, StationSearchOutcome outcome)
    {
        if ((outcome.Song ?? (track.VideoId is null ? outcome.OtherVersion : null)) is { } song)
            return new StationRow(track, StationMatch.Song, song);
        if (track.VideoId is { } videoId)
            return new StationRow(track, StationMatch.ServiceVideo, new TrackInfo
            {
                VideoId = videoId,
                Title = track.Title,
                Artists = [new ArtistRef(track.Artist)],
                Duration = track.Duration,
                // 16:9 with no bars; hqdefault is 4:3 letterboxed, and a square card keeps the bars.
                ThumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg",
            });
        return new StationRow(track, StationMatch.NotFound, null);
    }

    private static List<StationTrack> Distinct(IReadOnlyList<StationTrack> tracks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var distinct = new List<StationTrack>(tracks.Count);
        foreach (var track in tracks)
            if (seen.Add(StationMatchCache.KeyOf(track)))
                distinct.Add(track);
        return distinct;
    }
}
