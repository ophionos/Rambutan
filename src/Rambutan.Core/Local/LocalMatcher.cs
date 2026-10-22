using Rambutan.Core.Models;
using Rambutan.Core.Stations;

namespace Rambutan.Core.Local;

/// <summary>
/// What is searched for a local file: its title and artist tags, never its file
/// name or path, since the setting promises only those two. The duration stays
/// on this computer; it only ranks the results.
/// </summary>
public sealed record LocalMatchQuery(string Artist, string Title, TimeSpan? Duration)
{
    /// <summary>The query for a file's tags; null without a title tag, so a
    /// file named only by its file name is never searched.</summary>
    public static LocalMatchQuery? Of(LocalFileTags? tags) =>
        tags?.Title is { } title && !string.IsNullOrWhiteSpace(title)
            ? new LocalMatchQuery(tags.Artist ?? tags.AlbumArtist ?? "", title, tags.Duration)
            : null;

    /// <summary>The text sent to YouTube Music's song search.</summary>
    public string SearchText => Artist.Length > 0 ? $"{Artist} {Title}" : Title;

    /// <summary>Tags the matcher can't tell apart give the same key, so only a
    /// retag that could change the pick searches again.</summary>
    public string Key => StationMatchCache.KeyOf(new StationTrack(Artist, Title));
}

/// <summary>A search's answer for one local file: the same song on YouTube
/// Music, or null when there is none. <see cref="Key"/> is the query's.</summary>
public sealed record LocalMatch(string Key, TrackInfo? Song, DateTimeOffset SearchedAt);

public enum LocalMatchState
{
    /// <summary>Nothing is known or being searched; <see cref="LocalMatcher.Request"/> starts a search.</summary>
    NotSearched,
    /// <summary>The file has no title tag, so nothing is searched.</summary>
    NoTitle,
    Searching,
    Found,
    NotFound,
    /// <summary>The last search failed (offline); the next request tries again.</summary>
    Failed,
}

public readonly record struct LocalMatchStatus(LocalMatchState State, TrackInfo? Song = null);

/// <summary>
/// Finds the YouTube Music song that is a local file's recording, on demand:
/// one search of "artist title" per file, picked by <see cref="TrackMatcher"/>
/// without its last resort, since a like or a radio on a remix or a live take
/// would act on the wrong song. Answers are kept by local id with the query's
/// key, so a retagged file is searched again; a miss expires sooner than a
/// match, so a song YouTube Music adds later is found. A failed search is never
/// kept. Requests for a file already being searched share that search.
/// Thread-safe.
///
/// <paramref name="searchSongs"/> should be a signed-out catalog's song search,
/// as the stations use: these are searches the user never typed.
/// </summary>
public sealed class LocalMatcher(
    Func<string, CancellationToken, Task<IReadOnlyList<TrackInfo>>> searchSongs, TimeProvider? clock = null)
{
    public static readonly TimeSpan FoundLifetime = StationMatchCache.SongLifetime;
    public static readonly TimeSpan NotFoundLifetime = StationMatchCache.NoSongLifetime;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, LocalMatch> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Key, Task<LocalMatch?> Search)> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failed = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>A search for <c>id</c> ended, found or not, or failed. Raised on
    /// the search's thread.</summary>
    public event Action<string>? Changed;

    /// <summary>What is known for the file now; never starts a search.</summary>
    public LocalMatchStatus StatusOf(string id, LocalMatchQuery? query)
    {
        if (query is null)
            return new(LocalMatchState.NoTitle);
        lock (_lock)
        {
            if (Current(id, query.Key) is { } match)
                return match.Song is { } song ? new(LocalMatchState.Found, song) : new(LocalMatchState.NotFound);
            if (_pending.TryGetValue(id, out var pending) && pending.Key == query.Key)
                return new(LocalMatchState.Searching);
            return _failed.TryGetValue(id, out var failed) && failed == query.Key
                ? new(LocalMatchState.Failed)
                : new(LocalMatchState.NotSearched);
        }
    }

    /// <summary>
    /// The matched song: the kept answer while it is current, else a search.
    /// Null without a title tag or a match. A failed search throws, here and to
    /// every caller sharing it. Cancelling stops only this caller's wait.
    /// </summary>
    public async Task<TrackInfo?> MatchAsync(string id, LocalMatchQuery? query, CancellationToken ct = default)
    {
        if (query is null)
            return null;
        Task<LocalMatch?> search;
        lock (_lock)
        {
            if (Current(id, query.Key) is { } match)
                return match.Song;
            if (_pending.TryGetValue(id, out var pending) && pending.Key == query.Key)
            {
                search = pending.Search;
            }
            else
            {
                search = SearchAsync(id, query);
                _pending[id] = (query.Key, search);
            }
        }
        return (await search.WaitAsync(ct).ConfigureAwait(false))?.Song;
    }

    /// <summary>Starts a search when nothing current is known or pending; the
    /// answer arrives through <see cref="Changed"/>.</summary>
    public void Request(string id, LocalMatchQuery? query)
    {
        if (query is null)
            return;
        var search = MatchAsync(id, query);
        // Seen here so an unobserved failure doesn't surface later; it is in
        // StatusOf as Failed.
        _ = search.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private async Task<LocalMatch?> SearchAsync(string id, LocalMatchQuery query)
    {
        // Off the caller's thread and out of its lock: the search must not
        // complete inside MatchAsync's lock, nor on the UI thread.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        LocalMatch match;
        try
        {
            var results = await searchSongs(query.SearchText, CancellationToken.None).ConfigureAwait(false);
            var candidates = results.Count > StationResolver.CandidateCount
                ? results.Take(StationResolver.CandidateCount).ToList()
                : results;
            var song = TrackMatcher.Pick(new StationTrack(query.Artist, query.Title, query.Duration), candidates);
            match = new LocalMatch(query.Key, song, _clock.GetUtcNow());
            lock (_lock)
            {
                _known[id] = match;
                _failed.Remove(id);
                RemovePending(id, query.Key);
            }
        }
        catch
        {
            lock (_lock)
            {
                _failed[id] = query.Key;
                RemovePending(id, query.Key);
            }
            Changed?.Invoke(id);
            throw;
        }
        Changed?.Invoke(id);
        return match;
    }

    private void RemovePending(string id, string key)
    {
        if (_pending.TryGetValue(id, out var pending) && pending.Key == key)
            _pending.Remove(id);
    }

    /// <summary>The answers kept now, to save; expired ones are left out.</summary>
    public IReadOnlyList<KeyValuePair<string, LocalMatch>> Export()
    {
        lock (_lock)
            return _known.Where(e => !IsExpired(e.Value)).ToList();
    }

    /// <summary>Adds saved answers; expired ones are skipped, and one already
    /// known from this run wins.</summary>
    public void Import(IEnumerable<KeyValuePair<string, LocalMatch>> entries)
    {
        lock (_lock)
            foreach (var (id, match) in entries)
                if (!IsExpired(match))
                    _known.TryAdd(id, match);
    }

    private LocalMatch? Current(string id, string key) =>
        _known.TryGetValue(id, out var match) && match.Key == key && !IsExpired(match) ? match : null;

    private bool IsExpired(LocalMatch match)
    {
        var age = _clock.GetUtcNow() - match.SearchedAt;
        // A date in the future (a clock moved back) isn't trusted either.
        return age < TimeSpan.Zero || age > (match.Song is null ? NotFoundLifetime : FoundLifetime);
    }

    /// <summary>
    /// The local track as shown with what its tags lack taken from the match:
    /// the artists' and album's names, without their YouTube Music links, so
    /// the track still navigates and acts as the local file it is. The file
    /// itself is never written.
    /// </summary>
    public static TrackInfo Fill(TrackInfo local, TrackInfo? song)
    {
        if (song is null)
            return local;
        var artists = local.Artists.Count == 0 && song.Artists.Count > 0
            ? song.Artists.Select(a => new ArtistRef(a.Name)).ToArray()
            : null;
        var album = string.IsNullOrEmpty(local.Album) && !string.IsNullOrEmpty(song.Album) ? song.Album : null;
        if (artists is null && album is null)
            return local;
        return local with { Artists = artists ?? local.Artists, Album = album ?? local.Album };
    }
}
