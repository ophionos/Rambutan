using System.Text.Json;
using System.Text.Json.Serialization;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Downloads;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Settings;

namespace Rambutan.LocalLibrary;

/// <summary>
/// The downloads: audio files in the downloads folder, and their records in
/// <c>downloads.json</c> beside the settings. A download is kept apart from
/// <see cref="AudioDiskCache"/>, so the cache's size cap, its eviction and its
/// fault-retry deletes never touch one, and a cache set to zero still downloads.
/// <para>
/// Each record keeps its own file's full path. When the user picks another
/// folder, new downloads go there and existing ones stay where they are and keep
/// playing (maintainer's decision, 2026-10-03). A record whose file is gone
/// is shown as missing, never played.
/// </para>
/// Files are written as <c>.part</c> and renamed only when complete, like the cache.
/// <para>
/// A file the decoder still holds (the song is playing; Windows won't delete an
/// open file) can't go with its record. Its path is kept in the manifest as a
/// leftover and deleted later: at the next start, at the next change to the
/// downloads, or when <see cref="RetryLeftovers"/> is called as playback moves on.
/// The folder may hold the user's own files, so only remembered paths are deleted.
/// </para>
/// </summary>
public sealed class DownloadStore : IDownloadStore, IDisposable
{
    private const string PartExtension = ".part";
    private static readonly TimeSpan PendingSaveDelay = TimeSpan.FromSeconds(1);

    private readonly string _manifestPath;
    private readonly string _pendingPath;
    private readonly Func<string> _folder;
    private readonly IAudioFetcher _fetcher;
    private readonly Func<string, CancellationToken, Task<byte[]>>? _fetchArtwork;
    private readonly object _lock = new();
    private readonly Dictionary<string, SavedDownload> _downloads = new(StringComparer.Ordinal);
    private readonly List<string> _leftovers = [];

    private IReadOnlyList<PendingDownload>? _pendingToSave;
    private bool _pendingSaveScheduled;

    /// <param name="dataDirectory">Where <c>downloads.json</c> and the pending
    /// queue live.</param>
    /// <param name="folder">The folder new downloads go to, read at each download.</param>
    /// <param name="fetchArtwork">Fetches an artwork URL's bytes, so a download keeps
    /// its cover next to its audio; null keeps none.</param>
    public DownloadStore(
        string dataDirectory, Func<string> folder, IAudioFetcher fetcher,
        Func<string, CancellationToken, Task<byte[]>>? fetchArtwork = null)
    {
        _manifestPath = Path.Combine(dataDirectory, "downloads.json");
        _pendingPath = Path.Combine(dataDirectory, "downloads-pending.json");
        _folder = folder;
        _fetcher = fetcher;
        _fetchArtwork = fetchArtwork;

        var manifest = LoadManifest();
        foreach (var saved in manifest.Downloads)
            _downloads[saved.VideoId!] = saved;
        _leftovers.AddRange(manifest.LeftoverPaths ?? []);
        CleanStaleParts();
        RetryLeftovers();
    }

    /// <summary>Deletes a download's file; false when it can't (in use). Tests
    /// replace it, since no portable way holds a file open against deletion.</summary>
    internal Func<string, bool> DeleteFile { get; set; } = TryDelete;

    public IReadOnlyList<DownloadedTrack> List()
    {
        lock (_lock)
        {
            return _downloads.Values
                // Records from before the order was kept sort by when they finished.
                .OrderByDescending(d => d.RequestedAt ?? d.DownloadedAt)
                .ThenBy(d => d.Position ?? 0)
                .Select(d => new DownloadedTrack(
                    ToTrack(d), d.SizeBytes, d.DownloadedAt, IsMissing: !File.Exists(d.Path)))
                .ToArray();
        }
    }

    public bool Contains(string videoId)
    {
        lock (_lock)
            return _downloads.TryGetValue(videoId, out var d) && File.Exists(d.Path);
    }

    /// <summary>The download as something to play, or null when there is none or
    /// its file is gone. Any quality preference accepts it: the user asked for this
    /// file to be kept, and a local file costs no data.</summary>
    public ResolvedMedia? TryGetMedia(string videoId)
    {
        SavedDownload? d;
        lock (_lock)
            _downloads.TryGetValue(videoId, out d);

        return d is not null && File.Exists(d.Path)
            ? new ResolvedMedia(d.Path!, d.AudioCodec ?? "", d.LoudnessDb)
            : null;
    }

    /// <summary>The download's files, for export; null when there is no such
    /// download. A download whose file is gone has a null audio path.</summary>
    public ExportSource? ExportSourceFor(string videoId)
    {
        SavedDownload? d;
        lock (_lock)
            _downloads.TryGetValue(videoId, out d);

        return d is null
            ? null
            : new ExportSource(ToTrack(d), File.Exists(d.Path) ? d.Path : null, d.AudioCodec ?? "", ArtworkPathFor(videoId));
    }

    /// <summary>The download's own copy of its artwork, or null when it has none
    /// (made before covers were kept, or the cover couldn't be fetched).</summary>
    public string? ArtworkPathFor(string videoId)
    {
        string? path;
        lock (_lock)
            path = _downloads.TryGetValue(videoId, out var d) ? d.ArtworkPath : null;
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>Fetches the covers that downloads made before covers were kept, or
    /// whose cover failed, still lack. One at a time; a failure waits for the next run.</summary>
    public async Task FillMissingArtworkAsync(CancellationToken ct)
    {
        if (_fetchArtwork is null)
            return;
        List<SavedDownload> missing;
        lock (_lock)
        {
            missing = _downloads.Values
                .Where(d => d.ThumbnailUrl is not null && File.Exists(d.Path) &&
                            (d.ArtworkPath is null || !File.Exists(d.ArtworkPath)))
                .ToList();
        }

        var failures = 0;
        foreach (var d in missing)
        {
            var stem = Path.Combine(Path.GetDirectoryName(d.Path)!, Path.GetFileNameWithoutExtension(d.Path!));
            if (await SaveArtworkAsync(d.ThumbnailUrl, stem, ct) is not { } art)
            {
                // Offline, every fetch fails: stop rather than log one line per download.
                if (++failures >= 3)
                    return;
                continue;
            }
            failures = 0;
            lock (_lock)
            {
                // Removed or downloaded again meanwhile: the cover belongs to no record.
                if (!_downloads.TryGetValue(d.VideoId!, out var now) || !ReferenceEquals(now, d))
                {
                    if (!_downloads.Values.Any(r => SamePath(r.ArtworkPath, art)))
                        TryDelete(art);
                    continue;
                }
                d.ArtworkPath = art;
                SaveManifest();
            }
        }
    }

    /// <summary>The sum of the downloads' sizes, for Settings. Leftovers not yet
    /// deleted count too: they still take the space.</summary>
    public long TotalBytes
    {
        get
        {
            lock (_lock)
                return _downloads.Values.Sum(d => d.SizeBytes) + _leftovers.Sum(SizeOf);
        }
    }

    /// <summary>Deletes the files that couldn't be deleted with their records.
    /// Cheap when there are none; one still in use waits for the next try.</summary>
    public void RetryLeftovers()
    {
        lock (_lock)
        {
            if (_leftovers.Count == 0)
                return;
            // A path downloaded again since is a live file now, not a leftover.
            var done = _leftovers.RemoveAll(path =>
                _downloads.Values.Any(d => SamePath(d.Path, path) || SamePath(d.ArtworkPath, path)) ||
                !File.Exists(path) || DeleteFile(path));
            if (done > 0)
                SaveManifest();
        }
    }

    public async Task DownloadAsync(
        TrackInfo track, DownloadOrder order, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var folder = _folder();
        Directory.CreateDirectory(folder);
        var stem = Path.Combine(folder, Sanitize(track.VideoId));
        var partPath = stem + PartExtension;
        string? artwork = null;

        try
        {
            FetchedAudio fetched;
            long size;
            await using (var file = File.Create(partPath))
            {
                fetched = await _fetcher.FetchAsync(track, file, progress, ct);
                size = file.Length;
            }

            var finalPath = stem + ExtensionFor(fetched.AudioCodec);
            var record = FromTrack(track, order);
            record.Path = finalPath;
            record.ArtworkPath = artwork = await SaveArtworkAsync(track.ThumbnailUrl, stem, ct);
            record.AudioCodec = fetched.AudioCodec;
            record.Itag = fetched.Itag;
            record.Quality = AudioQualityNames.Format(fetched.Quality);
            record.LoudnessDb = fetched.LoudnessDb;
            record.SizeBytes = size;
            record.DownloadedAt = DateTimeOffset.UtcNow;

            lock (_lock)
            {
                File.Move(partPath, finalPath, overwrite: true);
                _leftovers.RemoveAll(path => SamePath(path, finalPath) || SamePath(path, artwork));
                // A download made again into another folder replaces the old files.
                if (_downloads.TryGetValue(track.VideoId, out var old))
                {
                    if (!SamePath(old.Path, finalPath))
                        DeleteOrRemember(old.Path!);
                    if (old.ArtworkPath is { } oldArt && !SamePath(oldArt, record.ArtworkPath))
                        DeleteOrRemember(oldArt);
                }
                _downloads[track.VideoId] = record;
                SaveManifest();
            }
            RetryLeftovers();
        }
        catch
        {
            TryDelete(partPath);
            // The cover went to a path no record uses (or an older record's, which
            // it then still shows correctly: same track, same cover).
            lock (_lock)
            {
                if (artwork is not null && !_downloads.Values.Any(r => SamePath(r.ArtworkPath, artwork)))
                    TryDelete(artwork);
            }
            throw;
        }
    }

    public void Delete(string videoId)
    {
        lock (_lock)
        {
            if (!_downloads.Remove(videoId, out var d))
                return;
            // The record goes even when the file can't (in playback, on Windows),
            // so the track stops counting as downloaded at once.
            DeleteOrRemember(d.Path!);
            if (d.ArtworkPath is { } art)
                DeleteOrRemember(art);
            SaveManifest();
        }
        RetryLeftovers();
    }

    /// <summary>Call under the lock; the caller saves the manifest.</summary>
    private void DeleteOrRemember(string path)
    {
        if (DeleteFile(path) || _leftovers.Any(p => SamePath(p, path)))
            return;
        AppLog.Write("downloads", $"couldn't delete {path} (in use?); will retry");
        _leftovers.Add(path);
    }

    private static bool SamePath(string? a, string? b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path) is { Exists: true } file ? file.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    public IReadOnlyList<PendingDownload> LoadPending()
    {
        try
        {
            if (!File.Exists(_pendingPath))
                return [];
            var saved = JsonSerializer.Deserialize(
                File.ReadAllText(_pendingPath), DownloadsJsonContext.Default.SavedPendingDownloads);
            if (saved is null || saved.Version is < 1 or > SavedPendingDownloads.CurrentVersion)
                return [];
            // Jobs saved before the order was kept become one request, in their saved order.
            var now = DateTimeOffset.UtcNow;
            return saved.Tracks.Where(IsValid)
                .Select((t, i) => new PendingDownload(ToTrack(t), new DownloadOrder(t.RequestedAt ?? now, t.Position ?? i)))
                .ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Write("downloads", $"couldn't read the pending downloads: {ex.Message}");
            return [];
        }
    }

    /// <summary>Saves within a second, coalescing the bursts a playlist's jobs
    /// make; <see cref="Dispose"/> writes whatever is still waiting.</summary>
    public void SavePending(IReadOnlyList<PendingDownload> pending)
    {
        lock (_lock)
        {
            _pendingToSave = pending;
            if (_pendingSaveScheduled)
                return;
            _pendingSaveScheduled = true;
        }

        _ = Task.Delay(PendingSaveDelay).ContinueWith(
            _ => FlushPending(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void FlushPending()
    {
        IReadOnlyList<PendingDownload>? pending;
        lock (_lock)
        {
            pending = _pendingToSave;
            _pendingToSave = null;
            _pendingSaveScheduled = false;
        }

        if (pending is null)
            return;
        try
        {
            if (pending.Count == 0)
            {
                File.Delete(_pendingPath);
                return;
            }
            var saved = new SavedPendingDownloads { Tracks = pending.Select(p => (SavedDownloadTrack)FromTrack(p.Track, p.Order)).ToList() };
            WriteAtomic(_pendingPath, JsonSerializer.Serialize(saved, DownloadsJsonContext.Default.SavedPendingDownloads));
        }
        catch (Exception ex)
        {
            AppLog.Write("downloads", $"couldn't save the pending downloads: {ex.Message}");
        }
    }

    public void Dispose() => FlushPending();

    private SavedDownloads LoadManifest()
    {
        try
        {
            if (!File.Exists(_manifestPath))
                return new SavedDownloads();
            var saved = JsonSerializer.Deserialize(
                File.ReadAllText(_manifestPath), DownloadsJsonContext.Default.SavedDownloads);
            if (saved is null || saved.Version is < 1 or > SavedDownloads.CurrentVersion)
                throw new InvalidDataException($"version {saved?.Version} isn't one this build reads");
            saved.Downloads = saved.Downloads.Where(d => IsValid(d) && !string.IsNullOrEmpty(d.Path)).ToList();
            saved.LeftoverPaths = saved.LeftoverPaths?.Where(p => !string.IsNullOrEmpty(p)).ToList();
            return saved;
        }
        catch (Exception ex)
        {
            // Fails closed: start with no downloads rather than guess at the file,
            // and set it aside so the next save can't destroy what it held.
            AppLog.Write("downloads", $"couldn't read downloads.json, set aside: {ex.Message}");
            try
            {
                File.Move(_manifestPath, _manifestPath + ".unreadable", overwrite: true);
            }
            catch
            {
            }
            return new SavedDownloads();
        }
    }

    /// <summary>Call under the lock.</summary>
    private void SaveManifest()
    {
        var saved = new SavedDownloads
        {
            Downloads = _downloads.Values.ToList(),
            LeftoverPaths = _leftovers.Count > 0 ? [.. _leftovers] : null,
        };
        WriteAtomic(_manifestPath, JsonSerializer.Serialize(saved, DownloadsJsonContext.Default.SavedDownloads));
    }

    private static void WriteAtomic(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Partial files a crash left in the current folder. Only our own
    /// <c>.part</c> files: the folder may be one the user also keeps other things in.</summary>
    private void CleanStaleParts()
    {
        try
        {
            var folder = _folder();
            if (!Directory.Exists(folder))
                return;
            foreach (var part in Directory.EnumerateFiles(folder, "*" + PartExtension))
                TryDelete(part);
        }
        catch
        {
        }
    }

    /// <summary>Writes the cover beside the audio as <c>&lt;stem&gt;.jpg</c> (or the
    /// format it came in) and returns its path; null when there is none to fetch or
    /// the fetch fails. A missing cover never fails the download: the remote art
    /// still shows while online.</summary>
    private async Task<string?> SaveArtworkAsync(string? url, string stem, CancellationToken ct)
    {
        if (_fetchArtwork is null || string.IsNullOrEmpty(url))
            return null;
        var partPath = $"{stem}.{Guid.NewGuid():N}{PartExtension}";
        try
        {
            var bytes = await _fetchArtwork(ArtworkUrls.Largest(url), ct);
            if (bytes.Length == 0)
                return null;
            await File.WriteAllBytesAsync(partPath, bytes, ct);
            var path = stem + ArtworkUrls.ExtensionFor(bytes);
            File.Move(partPath, path, overwrite: true);
            return path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(partPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(partPath);
            AppLog.Write("downloads", $"couldn't keep the cover for {Path.GetFileName(stem)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The container yt-dlp's audio formats come in, so the file opens
    /// elsewhere too; ffmpeg probes the content either way.</summary>
    internal static string ExtensionFor(string audioCodec) =>
        audioCodec.StartsWith("opus", StringComparison.OrdinalIgnoreCase) ? ".webm"
        : audioCodec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) ? ".m4a"
        : ".audio";

    private static bool TryDelete(string? path)
    {
        if (path is null)
            return true;
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsValid(SavedDownloadTrack? t) =>
        t is not null && !string.IsNullOrEmpty(t.VideoId) && !string.IsNullOrEmpty(t.Title);

    internal static TrackInfo ToTrack(SavedDownloadTrack t) => new()
    {
        VideoId = t.VideoId!,
        Title = t.Title!,
        Artists = (t.Artists ?? [])
            .Where(a => !string.IsNullOrEmpty(a?.Name))
            .Select(a => new ArtistRef(a.Name!, a.BrowseId))
            .ToArray(),
        Album = t.Album,
        AlbumBrowseId = string.IsNullOrEmpty(t.AlbumBrowseId) ? null : t.AlbumBrowseId,
        Duration = t.DurationSeconds is double s && s > 0 && double.IsFinite(s) ? TimeSpan.FromSeconds(s) : null,
        ThumbnailUrl = t.ThumbnailUrl,
        TrackNumber = t.TrackNumber is > 0 ? t.TrackNumber : null,
        SongId = string.IsNullOrEmpty(t.SongId) ? null : t.SongId,
    };

    /// <summary>A track as it is saved; no order, which only a download has.</summary>
    internal static SavedDownloadTrack TrackRecord(TrackInfo t) => new()
    {
        VideoId = t.VideoId,
        Title = t.Title,
        Artists = t.Artists.Select(a => new SavedDownloadArtist { Name = a.Name, BrowseId = a.BrowseId }).ToList(),
        Album = t.Album,
        AlbumBrowseId = t.AlbumBrowseId,
        DurationSeconds = t.Duration?.TotalSeconds,
        ThumbnailUrl = t.ThumbnailUrl,
        TrackNumber = t.TrackNumber,
        SongId = t.SongId,
    };

    private static SavedDownload FromTrack(TrackInfo t, DownloadOrder order) => new()
    {
        RequestedAt = order.RequestedAt,
        Position = order.Position,
        VideoId = t.VideoId,
        Title = t.Title,
        Artists = t.Artists.Select(a => new SavedDownloadArtist { Name = a.Name, BrowseId = a.BrowseId }).ToList(),
        Album = t.Album,
        AlbumBrowseId = t.AlbumBrowseId,
        DurationSeconds = t.Duration?.TotalSeconds,
        ThumbnailUrl = t.ThumbnailUrl,
        TrackNumber = t.TrackNumber,
        SongId = t.SongId,
    };

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

/// <summary><c>downloads.json</c>. Its own DTOs, not Core's <see cref="TrackInfo"/>,
/// so a model change can't silently change what's on disk.</summary>
public sealed class SavedDownloads
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<SavedDownload> Downloads { get; set; } = [];

    /// <summary>Files whose records are gone but which couldn't be deleted yet
    /// (in use). Optional, so a version 1 file without it reads the same.</summary>
    public List<string>? LeftoverPaths { get; set; }
}

/// <summary><c>downloads-pending.json</c>: the jobs not yet finished. Its own file
/// because it changes with every job, and the downloads list can be long.</summary>
public sealed class SavedPendingDownloads
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<SavedDownloadTrack> Tracks { get; set; } = [];
}

public class SavedDownloadTrack
{
    public string? VideoId { get; set; }
    public string? Title { get; set; }
    public List<SavedDownloadArtist>? Artists { get; set; }
    public string? Album { get; set; }
    public string? AlbumBrowseId { get; set; }
    public double? DurationSeconds { get; set; }
    public string? ThumbnailUrl { get; set; }

    /// <summary>The track's number on its album, when it came from an album page.
    /// Optional: records saved before it was kept have none.</summary>
    public int? TrackNumber { get; set; }

    /// <summary>
    /// <see cref="TrackInfo.SongId"/>, so an offline album or playlist copy still
    /// marks the playing song when it was started from its counterpart.
    /// Optional within version 1, as in the saved queue. The file stays keyed by
    /// videoId: the audio of one video is not the audio of its counterpart.
    /// </summary>
    public string? SongId { get; set; }

    /// <summary>The <see cref="DownloadOrder"/>. Optional: records and jobs saved
    /// before it was kept have neither, and sort by when they finished.</summary>
    public DateTimeOffset? RequestedAt { get; set; }
    public int? Position { get; set; }
}

public sealed class SavedDownload : SavedDownloadTrack
{
    /// <summary>The audio file's full path; see <see cref="DownloadStore"/> on folders.</summary>
    public string? Path { get; set; }
    public string? AudioCodec { get; set; }
    public string? Itag { get; set; }

    /// <summary>The quality tier it was fetched under, in the settings spelling.</summary>
    public string? Quality { get; set; }
    public double? LoudnessDb { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset DownloadedAt { get; set; }

    /// <summary>The cover's own copy beside the audio, so it shows offline and
    /// outlives the artwork cache. Null on downloads made before covers were kept,
    /// until <see cref="DownloadStore.FillMissingArtworkAsync"/> fetches them.</summary>
    public string? ArtworkPath { get; set; }
}

public sealed class SavedDownloadArtist
{
    public string? Name { get; set; }
    public string? BrowseId { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SavedDownloads))]
[JsonSerializable(typeof(SavedPendingDownloads))]
[JsonSerializable(typeof(SavedKeptCollections))]
internal sealed partial class DownloadsJsonContext : JsonSerializerContext;
