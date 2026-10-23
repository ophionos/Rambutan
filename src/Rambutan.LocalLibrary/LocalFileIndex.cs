using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Security.Cryptography;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Local;

namespace Rambutan.LocalLibrary;

/// <summary>
/// The profile's local library: the watched folders' audio files, kept in
/// <c>local-index.json</c> with their covers in <c>local-covers</c>, both in the
/// profile's folder. A scan lists the folders, compares sizes and modified times
/// with the index (<see cref="LocalScan.Plan"/>) and reads only what is new,
/// changed or back, on <see cref="LocalScan.ReadThreads"/> threads.
/// <see cref="Changed"/> is raised as files are read, so a list can fill while the
/// scan runs.
/// <para>
/// It also holds the app's downloads, for <see cref="DownloadStore"/>: a download
/// is a file whose entry has a <see cref="LocalFile.Download"/>, in the watched
/// folders or not. One that no scan has read isn't listed. Reading a file again
/// keeps the download its entry has at that moment.
/// </para>
/// <para>
/// Thread-safe. <see cref="Changed"/> is raised on a scan thread.
/// </para>
/// </summary>
public sealed class LocalFileIndex : IDisposable
{
    public const string CoverFolderName = "local-covers";

    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ChangedEvery = TimeSpan.FromMilliseconds(250);

    private readonly LocalIndexFile _file;
    private readonly string _coverDirectory;
    private readonly IMediaFileReader _reader;
    private readonly Func<CancellationToken, Task> _readerReady;
    private readonly Func<IReadOnlyList<string>> _folders;
    private readonly LocalPaths _paths;
    private readonly int _threads;
    private readonly Lock _lock = new();
    // Taken before _lock, never inside it: snapshot and write as one, so an older
    // snapshot can't land after a newer one.
    private readonly Lock _saveLock = new();
    private readonly Dictionary<string, LocalFile> _files = new(StringComparer.Ordinal);
    // The downloads' entries by YouTube id.
    private readonly Dictionary<string, string> _downloadIds = new(StringComparer.Ordinal);
    private readonly List<string> _leftovers = [];
    private bool _downloadsImported;
    // The new files a running scan has given ids to and not read yet, by path,
    // so a file opened meanwhile gets the same id.
    private readonly Dictionary<string, string> _reading;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private long _nextId;
    private LocalScanStatus _status = LocalScanStatus.Idle;
    private CancellationTokenSource? _scanCts;
    private IReadOnlyList<string>? _scanScope;
    private bool _dirty;

    /// <param name="dataDirectory">The profile's folder.</param>
    /// <param name="readerReady">Completes when <paramref name="reader"/> can be
    /// used (libav is set up when the sidecars are ready).</param>
    /// <param name="folders">The watched folders, read at each scan and listing.</param>
    /// <param name="queued">The ids the restored queue holds: opened files
    /// (<see cref="OpenAsync"/>) not among them are forgotten as the index loads,
    /// before anything else can use it. Null keeps them all.</param>
    public LocalFileIndex(
        string dataDirectory, IMediaFileReader reader, Func<CancellationToken, Task> readerReady,
        Func<IReadOnlyList<string>> folders, LocalPaths? paths = null, int? threads = null,
        Func<IReadOnlySet<string>>? queued = null)
    {
        _coverDirectory = Path.Combine(dataDirectory, CoverFolderName);
        _file = new LocalIndexFile(dataDirectory, _coverDirectory);
        _reader = reader;
        _readerReady = readerReady;
        _folders = folders;
        _paths = paths ?? LocalPaths.Platform;
        _reading = new Dictionary<string, string>(_paths.Comparer);
        _threads = threads ?? LocalScan.ReadThreads(Environment.ProcessorCount);

        var loaded = _file.Load();
        foreach (var file in loaded.Files)
            _files[file.Id] = file;
        _nextId = loaded.NextId;
        _leftovers.AddRange(loaded.Leftovers);
        _downloadsImported = loaded.DownloadsImported;
        foreach (var file in loaded.Files.Where(f => f.Download is not null))
        {
            var videoId = file.Download!.Track.VideoId;
            // Two entries can't both be one video's download: the newer one is.
            if (_downloadIds.TryGetValue(videoId, out var otherId))
            {
                var other = _files[otherId];
                var older = other.Download!.DownloadedAt >= file.Download.DownloadedAt ? file : other;
                ClearDownload(older);
                _dirty = true;
                if (ReferenceEquals(older, file))
                    continue;
            }
            _downloadIds[videoId] = file.Id;
        }
        if (queued is not null && _files.Values.Any(f => f.IsOpened))
            ForgetOpened(queued());
    }

    /// <summary>Raised when files or <see cref="Status"/> change; at most a few
    /// times a second while a scan runs.</summary>
    public event Action? Changed;

    public LocalScanStatus Status
    {
        get
        {
            lock (_lock)
                return _status;
        }
    }

    /// <summary>The files inside the watched folders, missing and unreadable ones
    /// included. Files of a folder no longer watched stay in the index, keeping
    /// their ids should it be watched again, but aren't listed. A file an export
    /// wrote is listed once, as its download, while that is listed too.</summary>
    public IReadOnlyList<LocalFile> Files()
    {
        var roots = Roots(_folders());
        lock (_lock)
            return _files.Values.Where(f => IsListed(f, roots)).ToList();
    }

    /// <summary>How many of <see cref="Files"/> are readable, unreadable and
    /// missing (a missing file counts only as missing), in one pass.</summary>
    public (int Readable, int Unreadable, int Missing) Counts()
    {
        var roots = Roots(_folders());
        int readable = 0, unreadable = 0, missing = 0;
        lock (_lock)
        {
            foreach (var file in _files.Values)
            {
                if (!IsListed(file, roots))
                    continue;
                if (file.IsMissing)
                    missing++;
                else if (file.IsUnreadable)
                    unreadable++;
                else
                    readable++;
            }
        }
        return (readable, unreadable, missing);
    }

    /// <summary>Call under the lock.</summary>
    private bool IsListed(LocalFile file, IReadOnlyList<string> roots) =>
        IsUnderListedRoot(file, roots)
        && !(file.Download is null && file.Tags?.YouTubeVideoId is { } videoId
            && FindDownloadLocked(videoId) is { IsMissing: false } download && IsUnderListedRoot(download, roots));

    private bool IsUnderListedRoot(LocalFile file, IReadOnlyList<string> roots) =>
        !file.IsUnreadDownload && roots.Any(r => _paths.IsUnder(file.Path, r));

    /// <summary>The file with this id, wherever it is; null when the index has none.</summary>
    public LocalFile? Find(string id)
    {
        lock (_lock)
            return _files.GetValueOrDefault(id);
    }

    /// <summary>
    /// Brings the index up to date with the watched folders. A scan asked for
    /// while one runs stops that one and starts over, so the latest folders win.
    /// Cancelling keeps what was read so far.
    /// </summary>
    public Task ScanAsync(CancellationToken ct = default) => ScanAsync(null, ct);

    /// <summary>
    /// Brings the index up to date with some watched folders: those inside
    /// <paramref name="folders"/> (null for all). A scan this one stops has its
    /// folders added, so they are still scanned (<see cref="LocalWatch.Merge"/>).
    /// </summary>
    public async Task ScanAsync(IReadOnlyList<string>? folders, CancellationToken ct = default)
    {
        CancellationTokenSource cts;
        IReadOnlyList<string>? scope;
        lock (_lock)
        {
            scope = _scanCts is null ? folders : LocalWatch.Merge(_scanScope, folders, _paths);
            _scanCts?.Cancel();
            cts = _scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _scanScope = scope;
        }

        await _scanGate.WaitAsync(CancellationToken.None);
        try
        {
            cts.Token.ThrowIfCancellationRequested();
            await RunScanAsync(scope, cts.Token);
        }
        finally
        {
            lock (_lock)
            {
                if (_scanCts == cts)
                {
                    _scanCts = null;
                    _scanScope = null;
                }
                _reading.Clear();
                _status = _status with { IsScanning = false };
            }
            SaveIfDirty();
            _scanGate.Release();
            cts.Dispose();
            RaiseChanged();
        }
    }

    private async Task RunScanAsync(IReadOnlyList<string>? scope, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        // Scanning from here: on a first run the reader can wait minutes for ffmpeg.
        lock (_lock)
            _status = new LocalScanStatus(true, 0, 0, _status.UnavailableFolders);
        RaiseChanged();
        await _readerReady(ct);

        var watched = Roots(_folders());
        var roots = LocalWatch.Within(watched, scope, _paths);

        var (found, pictureFiles, listed, notListed) = await Task.Run(() => List(roots, ct), ct);
        lock (_lock)
        {
            // A deleted download's file that couldn't go yet is no file of the user's.
            var leftovers = _leftovers.ToHashSet(_paths.Comparer);
            found.RemoveAll(f => leftovers.Contains(f.Path));
        }
        var pictures = pictureFiles
            .GroupBy(f => Path.GetDirectoryName(f.Path) ?? "", _paths.Comparer)
            .Select(g => LocalFolderCovers.Pick(g))
            .OfType<FoundFile>()
            .ToDictionary(p => Path.GetDirectoryName(p.Path) ?? "", _paths.Comparer);
        var copies = new ConcurrentDictionary<string, string?>(_paths.Comparer);

        LocalScanPlan plan;
        List<FileToRead> toRead;
        IReadOnlyList<string> unavailable;
        List<string> scanned;
        lock (_lock)
        {
            var emptied = LocalScan.EmptiedRoots(_files.Values.Select(f => f.Known), found, listed, _paths);
            scanned = listed.Except(emptied, _paths.Comparer).ToList();
            notListed.AddRange(emptied);
            plan = LocalScan.Plan(_files.Values.Select(f => f.Known), found, scanned, _paths, _nextId);
            // Taken now, so a scan that stops early never hands out the same id twice.
            _nextId = plan.NextId;
            foreach (var item in plan.ToRead.Where(r => r.IsNew))
                _reading[item.File.Path] = item.Id;
            foreach (var id in plan.NowMissing)
                _files[id] = _files[id] with { IsMissing = true };
            // Downloads in a watched folder are listed like the user's own files,
            // as before downloads were in the index, so they are read once too.
            toRead = [.. plan.ToRead, .. UnreadDownloads(found, plan.ToRead)];
            _dirty |= plan.NowMissing.Count > 0 || toRead.Count > 0;
            unavailable = LocalWatch.Unavailable(_status.UnavailableFolders, roots, notListed, watched, _paths);
            _status = new LocalScanStatus(true, 0, toRead.Count, unavailable);
        }
        RaiseChanged();

        var read = 0;
        var unreadable = 0;
        var lastSave = Stopwatch.GetTimestamp();
        var lastChanged = 0L;
        await Parallel.ForEachAsync(
            toRead, new ParallelOptions { MaxDegreeOfParallelism = _threads, CancellationToken = ct },
            (item, token) =>
            {
                var file = ReadFile(item, pictures, copies);
                if (file.IsUnreadable)
                    Interlocked.Increment(ref unreadable);

                bool save, raise;
                lock (_lock)
                {
                    _files[file.Id] = file with { Download = _files.GetValueOrDefault(file.Id)?.Download };
                    _dirty = true;
                    _status = _status with { Read = ++read };
                    var now = Stopwatch.GetTimestamp();
                    save = Stopwatch.GetElapsedTime(lastSave, now) >= SaveEvery;
                    if (save)
                        lastSave = now;
                    raise = Stopwatch.GetElapsedTime(lastChanged, now) >= ChangedEvery;
                    if (raise)
                        lastChanged = now;
                }
                if (save)
                    SaveIfDirty();
                if (raise)
                    RaiseChanged();
                return ValueTask.CompletedTask;
            });

        UpdateFolderCovers(scanned, pictures, copies);
        RemoveUnusedCovers();
        AppLog.Write("local",
            $"scan: {found.Count} files in {roots.Count} folders ({notListed.Count} unavailable), " +
            $"{toRead.Count} read ({unreadable} unreadable), {plan.NowMissing.Count} now missing, " +
            $"{Stopwatch.GetElapsedTime(started).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s");
    }

    /// <summary>Call under the lock. The downloads among <paramref name="found"/>
    /// that no scan has read and <paramref name="planned"/> doesn't read.</summary>
    private List<FileToRead> UnreadDownloads(List<FoundFile> found, IReadOnlyList<FileToRead> planned)
    {
        var unread = new Dictionary<string, LocalFile>(_paths.Comparer);
        foreach (var file in _files.Values.Where(f => f.IsUnreadDownload))
            unread.TryAdd(file.Path, file);
        if (unread.Count == 0)
            return [];
        var reading = planned.Select(r => r.File.Path).ToHashSet(_paths.Comparer);
        return found
            .Where(f => unread.ContainsKey(f.Path) && reading.Add(f.Path))
            .Select(f => new FileToRead(f, unread[f.Path].Id, IsNew: false))
            .ToList();
    }

    /// <summary>The audio files under each folder, and the pictures that may be a
    /// folder's cover. A folder that doesn't exist or can't be listed is
    /// unavailable, and left out of the scanned roots.</summary>
    private (List<FoundFile> Found, List<FoundFile> Pictures, List<string> Scanned, List<string> Unavailable) List(
        IReadOnlyList<string> roots, CancellationToken ct)
    {
        var found = new List<FoundFile>();
        var pictures = new List<FoundFile>();
        var scanned = new List<string>();
        var unavailable = new List<string>();
        // Hidden and system entries are skipped by hand, not by the options:
        // Windows Media Player keeps its folder.jpg and AlbumArt files hidden.
        const FileAttributes skipped = FileAttributes.Hidden | FileAttributes.System;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var files = new List<FoundFile>();
            var images = new List<FoundFile>();
            try
            {
                if (!Directory.Exists(root))
                    throw new DirectoryNotFoundException();
                var entries = new FileSystemEnumerable<(FoundFile File, bool IsAudio)>(root,
                    (ref FileSystemEntry e) => (new FoundFile(e.ToFullPath(), e.Length, e.LastWriteTimeUtc),
                        LocalScan.AudioExtensions.Contains(Path.GetExtension(e.FileName).ToString())),
                    options)
                {
                    ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & skipped) == 0,
                    ShouldIncludePredicate = (ref FileSystemEntry e) =>
                        !e.IsDirectory
                        && (((e.Attributes & skipped) == 0
                                && LocalScan.AudioExtensions.Contains(Path.GetExtension(e.FileName).ToString()))
                            || LocalFolderCovers.IsCandidate(e.FileName.ToString())),
                };
                foreach (var (file, isAudio) in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    (isAudio ? files : images).Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive pulled out halfway through counts as not there at all.
                unavailable.Add(root);
                continue;
            }
            found.AddRange(files);
            pictures.AddRange(images);
            scanned.Add(root);
        }
        return (found, pictures, scanned, unavailable);
    }

    /// <summary>
    /// Files the user opened or dropped, to play. <paramref name="paths"/> are
    /// files or folders; a folder gives its audio files, subfolders included, in
    /// path order. A file keeps the id the index has for its path, else gets a new
    /// one; a new file outside the watched folders is kept as opened, unlisted,
    /// while a queue holds it. A file is read again only when it changed.
    /// Files that aren't there or can't be read as audio are left out.
    /// </summary>
    /// <param name="listed">Told how many files were found under the paths,
    /// before any is read; not called when there are none. On any thread.</param>
    public async Task<IReadOnlyList<LocalFile>> OpenAsync(
        IReadOnlyList<string> paths, Action<int>? listed = null, CancellationToken ct = default)
    {
        await _readerReady(ct);
        var found = await Task.Run(() => ListOpened(paths, ct), ct);
        if (found.Count == 0)
            return [];
        listed?.Invoke(found.Count);
        var roots = Roots(_folders());

        var items = new List<(FileToRead Item, bool Read)>(found.Count);
        lock (_lock)
        {
            var byPath = new Dictionary<string, LocalFile>(_paths.Comparer);
            foreach (var file in _files.Values)
                byPath.TryAdd(file.Path, file);
            foreach (var file in found)
            {
                if (byPath.TryGetValue(file.Path, out var known))
                {
                    var unchanged = known is { IsMissing: false, IsUnreadDownload: false }
                                    && known.Size == file.Size && known.ModifiedAt == file.ModifiedAt;
                    items.Add((new FileToRead(file, known.Id, IsNew: false), !unchanged));
                }
                else if (_reading.TryGetValue(file.Path, out var id))
                    items.Add((new FileToRead(file, id, IsNew: false), true));
                else
                {
                    items.Add((new FileToRead(file, LocalIds.Format(_nextId++), IsNew: true), true));
                    _dirty = true;
                }
            }
        }

        var read = new LocalFile?[items.Count];
        await Task.Run(() =>
        {
            var pictures = FolderPictures(items.Where(x => x.Read).Select(x => x.Item.File.Path));
            var copies = new ConcurrentDictionary<string, string?>(_paths.Comparer);
            Parallel.For(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = _threads, CancellationToken = ct },
                i => read[i] = items[i].Read ? ReadFile(items[i].Item, pictures, copies) : null);
        }, ct);

        List<LocalFile> opened = [];
        lock (_lock)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i].Item;
                if (read[i] is { } file)
                {
                    var outside = !roots.Any(r => _paths.IsUnder(file.Path, r));
                    if (_files.TryGetValue(item.Id, out var was))
                        file = file with { IsOpened = was.IsOpened, Download = was.Download };
                    else if (outside && file.IsUnreadable)
                        continue;
                    else
                        file = file with { IsOpened = outside };
                    // A scan that read it meanwhile found the same file.
                    _files[item.Id] = file;
                    _dirty = true;
                }
                if (_files.GetValueOrDefault(item.Id) is { IsMissing: false, IsUnreadable: false } now)
                    opened.Add(now);
            }
        }
        SaveIfDirty();
        RaiseChanged();
        return opened;
    }

    /// <summary>
    /// Forgets the opened files whose ids aren't in <paramref name="kept"/>; their
    /// ids aren't given again. A file in a watched folder by now stays, as a
    /// scanned one. Only while nothing else uses the index: the cover sweep would
    /// race a read that has saved its cover and not its entry yet.
    /// </summary>
    private void ForgetOpened(IReadOnlySet<string> kept)
    {
        var roots = Roots(_folders());
        lock (_lock)
        {
            foreach (var file in _files.Values.Where(f => f.IsOpened && !kept.Contains(f.Id)).ToList())
            {
                if (file.Download is not null || roots.Any(r => _paths.IsUnder(file.Path, r)))
                    _files[file.Id] = file with { IsOpened = false };
                else
                    _files.Remove(file.Id);
                _dirty = true;
            }
        }
        SaveIfDirty();
        RemoveUnusedCovers();
    }

    /// <summary>The files under <paramref name="paths"/>, each once: a file as it
    /// is, a folder's audio files (<see cref="LocalScan.AudioExtensions"/>), not
    /// hidden ones, in path order. A path that isn't there is skipped.</summary>
    private List<FoundFile> ListOpened(IReadOnlyList<string> paths, CancellationToken ct)
    {
        const FileAttributes skipped = FileAttributes.Hidden | FileAttributes.System;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        // Ignoring case first on every platform, so "Zed" doesn't come before "alpha".
        var byPath = Comparer<FoundFile>.Create((a, b) =>
            StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path) is var c and not 0 ? c : string.CompareOrdinal(a.Path, b.Path));
        var seen = new HashSet<string>(_paths.Comparer);
        var found = new List<FoundFile>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var full = Path.GetFullPath(path);
                if (Directory.Exists(full))
                {
                    var files = new FileSystemEnumerable<FoundFile>(full,
                        (ref FileSystemEntry e) => new FoundFile(e.ToFullPath(), e.Length, e.LastWriteTimeUtc), options)
                    {
                        ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & skipped) == 0,
                        ShouldIncludePredicate = (ref FileSystemEntry e) =>
                            !e.IsDirectory && (e.Attributes & skipped) == 0
                            && LocalScan.AudioExtensions.Contains(Path.GetExtension(e.FileName).ToString()),
                    };
                    found.AddRange(files.Order(byPath).Where(f => seen.Add(f.Path)));
                }
                else if (new FileInfo(full) is { Exists: true } info && seen.Add(info.FullName))
                    found.Add(new FoundFile(info.FullName, info.Length, info.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or NotSupportedException)
            {
                AppLog.Write("local", $"couldn't open a file: {ex.GetType().Name}");
            }
        }
        return found;
    }

    /// <summary>The pictures that may be the covers of these files' folders, by
    /// folder, as a scan picks them; a folder that can't be listed has none.</summary>
    private Dictionary<string, FoundFile> FolderPictures(IEnumerable<string> files)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var pictures = new Dictionary<string, FoundFile>(_paths.Comparer);
        foreach (var folder in files.Select(f => Path.GetDirectoryName(f) ?? "").Distinct(_paths.Comparer))
        {
            try
            {
                var candidates = new DirectoryInfo(folder).EnumerateFiles("*", options)
                    .Where(f => LocalFolderCovers.IsCandidate(f.Name))
                    .Select(f => new FoundFile(f.FullName, f.Length, f.LastWriteTimeUtc));
                if (LocalFolderCovers.Pick(candidates) is { } picture)
                    pictures[folder] = picture;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
        return pictures;
    }

    private LocalFile ReadFile(
        FileToRead item, Dictionary<string, FoundFile> pictures, ConcurrentDictionary<string, string?> copies)
    {
        var tags = _reader.Read(item.File.Path);
        string? cover = null;
        FoundFile? fromFolder = null;
        if (tags?.Cover is { } embedded)
        {
            cover = SaveCover(embedded);
            tags = tags with { Cover = null };
        }
        else if (tags is not null && PictureFor(item.File.Path, pictures) is { } picture
                 && CopyFolderCover(picture, copies) is { } copy)
        {
            cover = copy;
            fromFolder = picture;
        }
        return new LocalFile(item.Id, item.File.Path, item.File.Size, item.File.ModifiedAt, tags, cover,
            FolderCover: fromFolder);
    }

    private static FoundFile? PictureFor(string audioPath, Dictionary<string, FoundFile> pictures) =>
        pictures.TryGetValue(Path.GetDirectoryName(audioPath) ?? "", out var picture) ? picture : null;

    /// <summary>
    /// Gives the files the scan didn't read their folder's picture as it is now:
    /// a picture added, changed or removed changes no audio file, so the plan
    /// doesn't read them. Files with an embedded cover keep it.
    /// </summary>
    private void UpdateFolderCovers(
        IReadOnlyList<string> scanned, Dictionary<string, FoundFile> pictures,
        ConcurrentDictionary<string, string?> copies)
    {
        List<(LocalFile File, FoundFile? Picture)> stale;
        lock (_lock)
        {
            stale = _files.Values
                .Where(f => f is { IsMissing: false, IsUnreadable: false }
                    && scanned.Any(root => _paths.IsUnder(f.Path, root)))
                .Select(f => (File: f, Picture: PictureFor(f.Path, pictures)))
                .Where(x => LocalFolderCovers.NeedsUpdate(x.File.CoverPath is not null, x.File.FolderCover, x.Picture))
                .ToList();
        }
        if (stale.Count == 0)
            return;

        var updated = stale.Select(x =>
        {
            var copy = x.Picture is { } picture ? CopyFolderCover(picture, copies) : null;
            return x.File with { CoverPath = copy, FolderCover = copy is null ? null : x.Picture };
        }).ToList();
        lock (_lock)
        {
            foreach (var file in updated)
            {
                // A file read again meanwhile has the newer answer.
                if (_files.TryGetValue(file.Id, out var now) && now.ModifiedAt == file.ModifiedAt && now.Size == file.Size)
                    _files[file.Id] = now with { CoverPath = file.CoverPath, FolderCover = file.FolderCover };
            }
            _dirty = true;
        }
    }

    /// <summary>The folder's picture in the cover cache, read once per scan; null
    /// when it can't be read.</summary>
    private string? CopyFolderCover(FoundFile picture, ConcurrentDictionary<string, string?> copies) =>
        copies.GetOrAdd(picture.Path, path =>
        {
            try
            {
                var extension = Path.GetExtension(path).ToLowerInvariant() is ".png" ? ".png" : ".jpg";
                return SaveCover(new LocalCover(File.ReadAllBytes(path), extension));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("local", $"couldn't read a folder's picture: {ex.GetType().Name}");
                return null;
            }
        });

    /// <summary>Covers are kept once per picture, named by its hash: an album's
    /// tracks usually carry the same one. Null when it can't be written; the
    /// track then shows without one.</summary>
    private string? SaveCover(LocalCover cover)
    {
        var name = Convert.ToHexStringLower(SHA256.HashData(cover.Data).AsSpan(0, 16)) + cover.Extension;
        var path = Path.Combine(_coverDirectory, name);
        if (File.Exists(path))
            return path;
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(_coverDirectory);
            File.WriteAllBytes(temp, cover.Data);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            // Two tracks of one album, read at once, write the same cover; Windows
            // refuses the second move while the first holds the name. The picture
            // is the same either way.
            if (File.Exists(path))
                return path;
            AppLog.Write("local", $"couldn't keep a cover: {ex.Message}");
            return null;
        }
    }

    /// <summary>Covers no file in the index uses any more, and leftovers of
    /// writes a crash cut short.</summary>
    private void RemoveUnusedCovers()
    {
        HashSet<string> used;
        lock (_lock)
            used = _files.Values.Select(f => f.CoverPath).OfType<string>().Select(Path.GetFileName).OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(_coverDirectory))
                return;
            foreach (var path in Directory.EnumerateFiles(_coverDirectory))
            {
                if (!used.Contains(Path.GetFileName(path)))
                    File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void SaveIfDirty()
    {
        lock (_saveLock)
        {
            List<LocalFile> files;
            long nextId;
            List<string> leftovers;
            bool imported;
            lock (_lock)
            {
                if (!_dirty)
                    return;
                _dirty = false;
                files = [.. _files.Values];
                nextId = _nextId;
                leftovers = [.. _leftovers];
                imported = _downloadsImported;
            }
            try
            {
                _file.Save(files, nextId, leftovers, imported);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (_lock)
                    _dirty = true;
                AppLog.Write("local", $"couldn't save {LocalIndexFile.FileName}: {ex.Message}");
            }
        }
    }

    // The downloads, for DownloadStore. Each change is saved before it returns:
    // a download's file whose record is lost would never be deleted.

    /// <summary>True once <c>downloads.json</c>'s records are in the index.</summary>
    internal bool DownloadsImported
    {
        get
        {
            lock (_lock)
                return _downloadsImported;
        }
    }

    /// <summary>The download's entry; null when the video has none.</summary>
    internal LocalFile? FindDownload(string videoId)
    {
        lock (_lock)
            return FindDownloadLocked(videoId);
    }

    internal IReadOnlyList<LocalFile> Downloads()
    {
        lock (_lock)
            return _downloadIds.Values.Select(id => _files[id]).ToList();
    }

    internal IReadOnlyList<string> Leftovers()
    {
        lock (_lock)
            return [.. _leftovers];
    }

    internal void SetLeftovers(IReadOnlyList<string> leftovers)
    {
        lock (_lock)
        {
            _leftovers.Clear();
            _leftovers.AddRange(leftovers);
            _dirty = true;
        }
        SaveIfDirty();
    }

    /// <summary>
    /// Records a finished download at <paramref name="path"/>, with the
    /// leftovers as they are now, in one save. An entry the index has for that
    /// path becomes the download, keeping its id and what was read of it; else
    /// the file gets a new id. The video's download before, when it was another
    /// file, no longer is one.
    /// </summary>
    internal void RecordDownload(
        string path, long size, DateTimeOffset modifiedAt, LocalDownload download, IReadOnlyList<string> leftovers)
    {
        lock (_lock)
        {
            Adopt(path, size, modifiedAt, download);
            _leftovers.Clear();
            _leftovers.AddRange(leftovers);
            _dirty = true;
        }
        SaveIfDirty();
        // An exported copy of the video may stop being listed.
        RaiseChanged();
    }

    /// <summary>The video's download stops being one, with the leftovers as
    /// they are now, in one save; false when there was none.</summary>
    internal bool RemoveDownload(string videoId, IReadOnlyList<string> leftovers)
    {
        lock (_lock)
        {
            if (FindDownloadLocked(videoId) is not { } removed)
                return false;
            _downloadIds.Remove(videoId);
            ClearDownload(removed);
            _leftovers.Clear();
            _leftovers.AddRange(leftovers);
            _dirty = true;
        }
        SaveIfDirty();
        // An exported copy of the video is listed again.
        RaiseChanged();
        return true;
    }

    /// <summary>Gives the download its cover, unless it changed since
    /// <paramref name="expected"/> (deleted, or downloaded again).</summary>
    internal bool SetDownloadArtwork(string videoId, LocalDownload expected, string artworkPath)
    {
        lock (_lock)
        {
            if (FindDownloadLocked(videoId) is not { } file || !ReferenceEquals(file.Download, expected))
                return false;
            _files[file.Id] = file with { Download = expected with { ArtworkPath = artworkPath } };
            _dirty = true;
        }
        SaveIfDirty();
        return true;
    }

    /// <summary>
    /// Takes in the records <c>downloads.json</c> kept: a video that has a
    /// download in the index already keeps it, and an entry for the same path
    /// becomes the download. The leftovers are added to the index's.
    /// </summary>
    internal void ImportDownloads(
        IReadOnlyList<(string Path, LocalDownload Download)> downloads, IReadOnlyList<string> leftovers)
    {
        var sizes = downloads.Select(d =>
        {
            // The records didn't keep the file's time, and the file hasn't
            // changed since the app wrote it.
            var info = new FileInfo(d.Path);
            return info.Exists ? (info.Length, new DateTimeOffset(info.LastWriteTimeUtc)) : (0L, default);
        }).ToList();
        lock (_lock)
        {
            for (var i = 0; i < downloads.Count; i++)
            {
                var (path, download) = downloads[i];
                if (!_downloadIds.ContainsKey(download.Track.VideoId))
                    Adopt(path, sizes[i].Item1, sizes[i].Item2, download);
            }
            foreach (var path in leftovers.Where(p => !_leftovers.Contains(p, _paths.Comparer)))
                _leftovers.Add(path);
            _downloadsImported = true;
            _dirty = true;
        }
        SaveIfDirty();
    }

    /// <summary>Call under the lock.</summary>
    private LocalFile? FindDownloadLocked(string videoId) =>
        _downloadIds.TryGetValue(videoId, out var id) && _files.GetValueOrDefault(id) is { } file
            && file.Download?.Track.VideoId == videoId
            ? file
            : null;

    /// <summary>Call under the lock; see <see cref="RecordDownload"/>.</summary>
    private void Adopt(string path, long size, DateTimeOffset modifiedAt, LocalDownload download)
    {
        var videoId = download.Track.VideoId;
        var before = FindDownloadLocked(videoId);
        var known = _files.Values.FirstOrDefault(f => _paths.Comparer.Equals(f.Path, path));
        LocalFile file;
        if (known is not null)
        {
            // Its size and time stay, so a file the download replaced is read again.
            if (known.Download is { } other && other.Track.VideoId != videoId)
                _downloadIds.Remove(other.Track.VideoId);
            file = known with { IsMissing = false, Download = download };
        }
        else
        {
            // A scan that has found the file and not read it yet gave it an id.
            var id = _reading.TryGetValue(path, out var reserved) ? reserved : LocalIds.Format(_nextId++);
            file = new LocalFile(id, path, size, modifiedAt, null, Download: download);
        }
        if (before is not null && before.Id != file.Id)
            ClearDownload(before);
        _files[file.Id] = file;
        _downloadIds[videoId] = file.Id;
    }

    /// <summary>Call under the lock. An entry that was read stays as the file it
    /// is; one that was only ever a download goes.</summary>
    private void ClearDownload(LocalFile file)
    {
        if (file.Tags is not null)
            _files[file.Id] = file with { Download = null };
        else
            _files.Remove(file.Id);
    }

    /// <summary>The watched folders as full paths, without repeats. Blank or
    /// malformed entries are skipped.</summary>
    private List<string> Roots(IReadOnlyList<string> folders)
    {
        var roots = new List<string>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;
            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!roots.Contains(full, _paths.Comparer))
                roots.Add(full);
        }
        return roots;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
        lock (_lock)
            _scanCts?.Cancel();
        SaveIfDirty();
    }
}
