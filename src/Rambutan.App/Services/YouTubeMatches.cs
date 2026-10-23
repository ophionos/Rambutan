using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.Core.Stations;

namespace Rambutan.App.Services;

/// <summary>
/// The local files' matches kept between runs, <c>local-matches.json</c> in the
/// profile's folder, keyed by local id. Answers made under other matcher rules
/// (<see cref="TrackMatcher.RulesVersion"/>) are dropped.
/// </summary>
public sealed class SavedLocalMatches
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public int Rules { get; set; }
    public List<SavedLocalMatch> Entries { get; set; } = [];
}

public sealed class SavedLocalMatch
{
    /// <summary>The local file's <c>local:&lt;n&gt;</c> id.</summary>
    public string? Id { get; set; }
    /// <summary><see cref="LocalMatchQuery.Key"/>: the normalized tags searched.</summary>
    public string? Key { get; set; }
    public DateTimeOffset SearchedAt { get; set; }
    /// <summary>Null when nothing matched.</summary>
    public SavedTrack? Song { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SavedLocalMatches))]
internal sealed partial class LocalMatchJsonContext : JsonSerializerContext;

/// <summary>
/// Local files matched to YouTube Music songs, only while the setting is on
/// (off by default: each search sends a file's artist and title tags to YouTube
/// Music). A file is searched when it plays, when its menu opens and when
/// autoplay needs its radio; never all at once. Off, nothing is searched and no
/// kept match is used.
///
/// A match gives a file what its tags lack, as shown (<see cref="Fill"/>), and
/// its cover when it has none (<see cref="CoverPathFor"/>): a copy kept in
/// <c>local-match-covers</c>, so it shows offline too. The queue's tracks stay
/// as they are, so turning the setting off takes it all back, and deletes the
/// kept covers.
///
/// A file whose tags name its song (<see cref="LocalTagValues.YouTubeVideoIdKey"/>,
/// as an export writes them) has that song as its match, whatever the setting,
/// and is never searched: nothing is sent that the file didn't already say.
/// </summary>
/// <param name="find">The indexed file for a local id.</param>
/// <param name="fetchCover">A cover's bytes by URL (the artwork cache).</param>
/// <param name="downloaded">The track a video's download keeps; null without one.
/// A tagged file's song is that track while it exists, else made from the tags.</param>
public sealed class YouTubeMatches(
    LocalMatcher matcher, Func<string, LocalFile?> find, string dataDirectory,
    Func<string, CancellationToken, Task<byte[]>> fetchCover, Func<string, TrackInfo?>? downloaded = null)
{
    private readonly string _path = Path.Combine(dataDirectory, "local-matches.json");
    private readonly string _covers = Path.Combine(dataDirectory, "local-match-covers");
    private readonly Lock _lock = new();
    // Writes and removals of kept covers, one at a time, so a removal never
    // takes a cover a write is about to finish.
    private readonly SemaphoreSlim _coverFiles = new(1, 1);
    private bool _enabled;
    private bool _loaded;
    private bool _following;

    /// <summary>A match was found, missed or failed, its cover arrived, or the
    /// setting changed: what is shown for local files may differ. Raised on any
    /// thread.</summary>
    public event Action? Changed;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            if (value)
                Follow();
            else
                _ = Task.Run(RemoveUnusedCoversAsync);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// What is known for the track's file, with a search started when nothing
    /// is (so that reads as searching). Null while the setting is off, unless
    /// the file's tags name its song, or for a track that isn't a local file.
    /// </summary>
    public LocalMatchStatus? StatusOf(TrackInfo track)
    {
        if (Tagged(track.VideoId) is { } tagged)
            return new LocalMatchStatus(LocalMatchState.Found, tagged);
        if (!Enabled || !LocalIds.IsLocal(track.VideoId))
            return null;
        Load();
        var query = QueryOf(track.VideoId);
        var status = matcher.StatusOf(track.VideoId, query);
        if (status.State is LocalMatchState.NotSearched or LocalMatchState.Failed)
        {
            matcher.Request(track.VideoId, query);
            status = matcher.StatusOf(track.VideoId, query);
        }
        return status;
    }

    /// <summary>Starts a search for the track's file if nothing is known yet.</summary>
    public void Request(TrackInfo? track)
    {
        if (track is not null)
            StatusOf(track);
    }

    /// <summary>The matched song's id, for autoplay's radio; waits for a search
    /// under way. Null while off (unless the tags name the song), or without a
    /// match. Throws when the search fails.</summary>
    public async Task<string?> RadioSeedAsync(TrackInfo track, CancellationToken ct)
    {
        if (Tagged(track.VideoId) is { } tagged)
            return tagged.VideoId;
        if (!Enabled || !LocalIds.IsLocal(track.VideoId))
            return null;
        Load();
        return (await matcher.MatchAsync(track.VideoId, QueryOf(track.VideoId), ct))?.VideoId;
    }

    /// <summary>The track with what its tags lack filled from its match, if it
    /// has one already; never starts a search.</summary>
    public TrackInfo Fill(TrackInfo track) =>
        Known(track.VideoId) is { } song ? LocalMatcher.Fill(track, song) : track;

    /// <summary>The kept copy of the match's cover, for a file that has no cover
    /// of its own; null otherwise.</summary>
    public string? CoverPathFor(string id) =>
        Known(id) is { } song && find(id) is { CoverPath: null } && CoverFile(song) is { } path && File.Exists(path)
            ? path
            : null;

    private TrackInfo? Known(string id)
    {
        if (Tagged(id) is { } tagged)
            return tagged;
        if (!Enabled || !LocalIds.IsLocal(id))
            return null;
        Load();
        var status = matcher.StatusOf(id, QueryOf(id));
        return status.State == LocalMatchState.Found ? status.Song : null;
    }

    private TrackInfo? Tagged(string id) =>
        LocalIds.IsLocal(id) && find(id) is { IsMissing: false, Tags: { YouTubeVideoId: { } videoId } tags } file
            ? downloaded?.Invoke(videoId)
                ?? LocalTracks.ToTrack(file) with { VideoId = videoId, SongId = tags.YouTubeSongId }
            : null;

    private LocalMatchQuery? QueryOf(string id) =>
        find(id) is { IsMissing: false } file ? LocalMatchQuery.Of(file.Tags) : null;

    // Named by the song, so files that match one song share its copy, and a
    // retag that changes the match changes the cover.
    private string? CoverFile(TrackInfo song) =>
        YouTubeIds.IsVideoId(song.VideoId) ? Path.Combine(_covers, song.VideoId + ".jpg") : null;

    private void Follow()
    {
        lock (_lock)
        {
            if (_following)
                return;
            _following = true;
        }
        matcher.Changed += id => _ = OnMatchedAsync(id);
    }

    private async Task OnMatchedAsync(string id)
    {
        Save();
        if (Known(id) is { ThumbnailUrl: { } url } song && find(id) is { CoverPath: null }
            && CoverFile(song) is { } path && !File.Exists(path))
        {
            try
            {
                var bytes = await fetchCover(ArtworkUrls.Largest(url), CancellationToken.None).ConfigureAwait(false);
                await _coverFiles.WaitAsync().ConfigureAwait(false);
                try
                {
                    Directory.CreateDirectory(_covers);
                    var temp = path + ".tmp";
                    await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
                    File.Move(temp, path, overwrite: true);
                }
                finally
                {
                    _coverFiles.Release();
                }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
            {
                AppLog.Write("local", $"match cover unavailable: {ex.GetType().Name}");
            }
        }
        // A file matched to another song may leave its old cover unused, and a
        // cover that arrived after the setting went off goes with the rest.
        await RemoveUnusedCoversAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>
    /// Deletes the kept covers that no current match uses for a file without a
    /// cover of its own, and leftovers of writes a crash cut short; while the
    /// setting is off, all of them. A file that is missing keeps its match's
    /// cover, since it can come back. Never throws.
    /// </summary>
    public async Task RemoveUnusedCoversAsync()
    {
        await _coverFiles.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_covers))
                return;
            if (!Enabled)
            {
                Directory.Delete(_covers, recursive: true);
                return;
            }
            Load();
            var used = matcher.Export()
                .Where(e => find(e.Key) is { CoverPath: null } file && LocalMatchQuery.Of(file.Tags)?.Key == e.Value.Key)
                .Select(e => e.Value.Song is { } song ? CoverFile(song) : null)
                .OfType<string>()
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(_covers))
            {
                if (!used.Contains(Path.GetFileName(path)))
                    File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("local", $"couldn't remove unused match covers: {ex.GetType().Name}");
        }
        finally
        {
            _coverFiles.Release();
        }
    }

    // Under the lock throughout, so nothing reads the matcher before the saved
    // answers are in and searches a file they answer.
    private void Load()
    {
        lock (_lock)
        {
            if (!_loaded)
            {
                _loaded = true;
                LoadSaved();
            }
        }
    }

    private void LoadSaved()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var saved = JsonSerializer.Deserialize(File.ReadAllText(_path), LocalMatchJsonContext.Default.SavedLocalMatches);
            if (saved is null || saved.Version is < 1 or > SavedLocalMatches.CurrentVersion)
                return;
            if (saved.Rules != TrackMatcher.RulesVersion)
            {
                AppLog.Write("local", $"Dropping saved matches from matcher rules {saved.Rules}; this build uses {TrackMatcher.RulesVersion}.");
                return;
            }
            matcher.Import(saved.Entries
                .Where(e => LocalIds.IsLocal(e.Id) && !string.IsNullOrEmpty(e.Key))
                .Select(e => new KeyValuePair<string, LocalMatch>(
                    e.Id!, new LocalMatch(e.Key!, e.Song?.ToTrack(), e.SearchedAt))));
        }
        catch (Exception ex)
        {
            AppLog.Write("local", $"Couldn't read the saved matches: {ex.GetType().Name}");
        }
    }

    private void Save()
    {
        var saved = new SavedLocalMatches
        {
            Rules = TrackMatcher.RulesVersion,
            Entries = matcher.Export().Select(e => new SavedLocalMatch
            {
                Id = e.Key,
                Key = e.Value.Key,
                SearchedAt = e.Value.SearchedAt,
                Song = e.Value.Song is { } song ? SavedTrack.From(song) : null,
            }).ToList(),
        };
        try
        {
            lock (_lock)
                StationSnapshotStore.WriteAtomically(_path, JsonSerializer.Serialize(saved, LocalMatchJsonContext.Default.SavedLocalMatches));
        }
        catch (Exception ex)
        {
            AppLog.Write("local", $"Couldn't save the matches: {ex.GetType().Name}");
        }
    }
}
