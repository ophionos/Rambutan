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
    private readonly Dictionary<string, LocalFile> _files = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private long _nextId;
    private LocalScanStatus _status = LocalScanStatus.Idle;
    private CancellationTokenSource? _scanCts;
    private bool _dirty;

    /// <param name="dataDirectory">The profile's folder.</param>
    /// <param name="readerReady">Completes when <paramref name="reader"/> can be
    /// used (libav is set up when the sidecars are ready).</param>
    /// <param name="folders">The watched folders, read at each scan and listing.</param>
    public LocalFileIndex(
        string dataDirectory, IMediaFileReader reader, Func<CancellationToken, Task> readerReady,
        Func<IReadOnlyList<string>> folders, LocalPaths? paths = null, int? threads = null)
    {
        _coverDirectory = Path.Combine(dataDirectory, CoverFolderName);
        _file = new LocalIndexFile(dataDirectory, _coverDirectory);
        _reader = reader;
        _readerReady = readerReady;
        _folders = folders;
        _paths = paths ?? LocalPaths.Platform;
        _threads = threads ?? LocalScan.ReadThreads(Environment.ProcessorCount);

        var (files, nextId) = _file.Load();
        foreach (var file in files)
            _files[file.Id] = file;
        _nextId = nextId;
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
    /// their ids should it be watched again, but aren't listed.</summary>
    public IReadOnlyList<LocalFile> Files()
    {
        var roots = Roots(_folders());
        lock (_lock)
            return _files.Values.Where(f => roots.Any(r => _paths.IsUnder(f.Path, r))).ToList();
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
                if (!roots.Any(r => _paths.IsUnder(file.Path, r)))
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
    public async Task ScanAsync(CancellationToken ct = default)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _scanCts?.Cancel();
            cts = _scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        await _scanGate.WaitAsync(CancellationToken.None);
        try
        {
            cts.Token.ThrowIfCancellationRequested();
            await RunScanAsync(cts.Token);
        }
        finally
        {
            lock (_lock)
            {
                if (_scanCts == cts)
                    _scanCts = null;
                _status = _status with { IsScanning = false };
            }
            SaveIfDirty();
            _scanGate.Release();
            cts.Dispose();
            RaiseChanged();
        }
    }

    private async Task RunScanAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        // Scanning from here: on a first run the reader can wait minutes for ffmpeg.
        lock (_lock)
            _status = new LocalScanStatus(true, 0, 0, []);
        RaiseChanged();
        await _readerReady(ct);

        var roots = Roots(_folders());

        var (found, scanned, unavailable) = await Task.Run(() => List(roots, ct), ct);

        LocalScanPlan plan;
        lock (_lock)
        {
            plan = LocalScan.Plan(_files.Values.Select(f => f.Known), found, scanned, _paths, _nextId);
            // Taken now, so a scan that stops early never hands out the same id twice.
            _nextId = plan.NextId;
            foreach (var id in plan.NowMissing)
                _files[id] = _files[id] with { IsMissing = true };
            _dirty |= plan.NowMissing.Count > 0 || plan.ToRead.Count > 0;
            _status = new LocalScanStatus(true, 0, plan.ToRead.Count, unavailable);
        }
        RaiseChanged();

        var read = 0;
        var unreadable = 0;
        var lastSave = Stopwatch.GetTimestamp();
        var lastChanged = 0L;
        await Parallel.ForEachAsync(
            plan.ToRead, new ParallelOptions { MaxDegreeOfParallelism = _threads, CancellationToken = ct },
            (item, token) =>
            {
                var file = ReadFile(item);
                if (file.IsUnreadable)
                    Interlocked.Increment(ref unreadable);

                bool save, raise;
                lock (_lock)
                {
                    _files[file.Id] = file;
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

        RemoveUnusedCovers();
        AppLog.Write("local",
            $"scan: {found.Count} files in {roots.Count} folders ({unavailable.Count} unavailable), " +
            $"{plan.ToRead.Count} read ({unreadable} unreadable), {plan.NowMissing.Count} now missing, " +
            $"{Stopwatch.GetElapsedTime(started).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s");
    }

    /// <summary>The audio files under each folder. A folder that doesn't exist or
    /// can't be listed is unavailable, and left out of the scanned roots.</summary>
    private (List<FoundFile> Found, List<string> Scanned, List<string> Unavailable) List(
        IReadOnlyList<string> roots, CancellationToken ct)
    {
        var found = new List<FoundFile>();
        var scanned = new List<string>();
        var unavailable = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var files = new List<FoundFile>();
            try
            {
                if (!Directory.Exists(root))
                    throw new DirectoryNotFoundException();
                var entries = new FileSystemEnumerable<FoundFile>(root,
                    (ref FileSystemEntry e) => new FoundFile(e.ToFullPath(), e.Length, e.LastWriteTimeUtc),
                    options)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry e) =>
                        !e.IsDirectory && LocalScan.AudioExtensions.Contains(Path.GetExtension(e.FileName).ToString()),
                };
                foreach (var file in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    files.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive pulled out halfway through counts as not there at all.
                unavailable.Add(root);
                continue;
            }
            found.AddRange(files);
            scanned.Add(root);
        }
        return (found, scanned, unavailable);
    }

    private LocalFile ReadFile(FileToRead item)
    {
        var tags = _reader.Read(item.File.Path);
        string? cover = null;
        if (tags?.Cover is { } picture)
        {
            cover = SaveCover(picture);
            tags = tags with { Cover = null };
        }
        return new LocalFile(item.Id, item.File.Path, item.File.Size, item.File.ModifiedAt, tags, cover);
    }

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
        List<LocalFile> files;
        long nextId;
        lock (_lock)
        {
            if (!_dirty)
                return;
            _dirty = false;
            files = [.. _files.Values];
            nextId = _nextId;
        }
        try
        {
            _file.Save(files, nextId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_lock)
                _dirty = true;
            AppLog.Write("local", $"couldn't save {LocalIndexFile.FileName}: {ex.Message}");
        }
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
