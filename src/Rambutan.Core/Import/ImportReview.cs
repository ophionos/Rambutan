using Rambutan.Core.Models;

namespace Rambutan.Core.Import;

/// <summary>Where an import creates its playlists.</summary>
public enum ImportTarget
{
    /// <summary>On the signed-in account, as private playlists.</summary>
    YouTubeMusic,

    /// <summary>As local playlists on this computer.</summary>
    ThisComputer,
}

/// <summary>
/// The review between matching and creating: what matching found, with the
/// user's changes over it. A change is made for a song, not a row, so a song a
/// list holds twice, or two lists hold, changes everywhere at once. Nothing is
/// written until the playlists are created. Not thread-safe: the UI thread's.
/// </summary>
public sealed class ImportReview(IReadOnlyList<ImportedList> lists)
{
    private readonly Dictionary<string, ImportMatch> _matches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrackInfo> _chosen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _leftOut = new(StringComparer.Ordinal);

    public IReadOnlyList<ImportedList> Lists { get; } = lists;

    /// <summary>Raised after any change, with the song's key; null when many changed.</summary>
    public event Action<string?>? Changed;

    /// <summary>Takes matching's answers; a later answer for a song replaces an
    /// earlier one (searching again for the songs that failed).</summary>
    public void Add(IReadOnlyDictionary<string, ImportMatch> matches)
    {
        foreach (var (key, match) in matches)
            _matches[key] = match;
        Changed?.Invoke(null);
    }

    /// <summary>What matching found for a song; <see cref="ImportMatch.Failed"/>
    /// before it was searched.</summary>
    public ImportMatch MatchOf(ImportedTrack track) =>
        _matches.GetValueOrDefault(ImportMatcher.KeyOf(track), ImportMatch.Failed);

    /// <summary>The song a row adds: the user's pick, else matching's; null when
    /// the row is left out or has no song.</summary>
    public TrackInfo? SongOf(ImportedTrack track)
    {
        var key = ImportMatcher.KeyOf(track);
        if (_leftOut.Contains(key))
            return null;
        return _chosen.TryGetValue(key, out var chosen) ? chosen : _matches.GetValueOrDefault(key)?.Song;
    }

    public bool IsChosen(ImportedTrack track) => _chosen.ContainsKey(ImportMatcher.KeyOf(track));

    public bool IsLeftOut(ImportedTrack track) => _leftOut.Contains(ImportMatcher.KeyOf(track));

    /// <summary>The user's pick for a song, over matching's.</summary>
    public void Choose(ImportedTrack track, TrackInfo song)
    {
        var key = ImportMatcher.KeyOf(track);
        _chosen[key] = song;
        _leftOut.Remove(key);
        Changed?.Invoke(key);
    }

    /// <summary>Leaves a song out of every list, or puts it back.</summary>
    public void SetLeftOut(ImportedTrack track, bool leftOut)
    {
        var key = ImportMatcher.KeyOf(track);
        if (leftOut ? _leftOut.Add(key) : _leftOut.Remove(key))
            Changed?.Invoke(key);
    }

    /// <summary>The distinct songs of <paramref name="lists"/> whose search failed
    /// or hasn't run, to search (again).</summary>
    public IReadOnlyList<ImportedTrack> Unsearched(IEnumerable<ImportedList> lists) =>
        lists.SelectMany(l => l.Tracks)
            .Where(t => MatchOf(t).State == ImportMatchState.Failed)
            .DistinctBy(ImportMatcher.KeyOf, StringComparer.Ordinal)
            .ToList();

    /// <summary>The songs a list adds, in its order, duplicates kept.</summary>
    public IReadOnlyList<TrackInfo> SongsOf(IEnumerable<ImportedTrack> tracks) =>
        tracks.Select(SongOf).OfType<TrackInfo>().ToList();

    public ImportCounts CountsOf(IEnumerable<ImportedTrack> tracks)
    {
        int found = 0, notFound = 0, failed = 0, leftOut = 0;
        foreach (var track in tracks)
        {
            if (IsLeftOut(track))
                leftOut++;
            else if (SongOf(track) is not null)
                found++;
            else if (MatchOf(track).State == ImportMatchState.NotFound)
                notFound++;
            else
                failed++;
        }
        return new ImportCounts(found, notFound, failed, leftOut);
    }
}

/// <summary>A list's rows by what they add: a song, nothing found, a search
/// that failed, or left out by the user.</summary>
public readonly record struct ImportCounts(int Found, int NotFound, int Failed, int LeftOut);
