using System.Globalization;

namespace Rambutan.Core.Local;

/// <summary>
/// The ids local files play under: <c>local:</c> and a number. The colon keeps
/// them apart from YouTube's ids (<see cref="Models.YouTubeIds"/>), so none is
/// ever sent there. A number is given once: a file keeps its id while it stays
/// at its path, and no other file gets it after it, since the queue tells tracks
/// apart by id.
/// </summary>
public static class LocalIds
{
    public const string Prefix = "local:";

    public static string Format(long number) => Prefix + number.ToString(CultureInfo.InvariantCulture);

    public static bool IsLocal(string? id) => id is not null && id.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>
/// How paths compare: without case on Windows, whose file systems ignore it, and
/// exactly elsewhere. Paths reach here already full (no "..", no relative parts).
/// </summary>
public sealed class LocalPaths(bool ignoreCase, char separator, char? altSeparator = null)
{
    public static LocalPaths Platform { get; } = OperatingSystem.IsWindows()
        ? new LocalPaths(ignoreCase: true, '\\', '/')
        : new LocalPaths(ignoreCase: false, '/');

    public StringComparer Comparer { get; } = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private StringComparison Comparison => ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or
    /// lies anywhere inside it. "C:\Music2" is not inside "C:\Music".</summary>
    public bool IsUnder(string path, string root)
    {
        var trimmed = root.TrimEnd(separator);
        if (altSeparator is { } alt)
            trimmed = trimmed.TrimEnd(alt);
        if (!path.StartsWith(trimmed, Comparison))
            return false;
        if (path.Length == trimmed.Length)
            return true;
        var next = path[trimmed.Length];
        return next == separator || next == altSeparator;
    }
}

/// <summary>A file the scan found, with what the file system says of it.</summary>
public readonly record struct FoundFile(string Path, long Size, DateTimeOffset ModifiedAt);

/// <summary>A file the index has, with what was seen of it when it was last read.</summary>
public readonly record struct KnownFile(string Id, string Path, long Size, DateTimeOffset ModifiedAt, bool IsMissing);

/// <param name="IsNew">True when the file wasn't in the index; false when it
/// changed or came back, and keeps its <paramref name="Id"/>.</param>
public readonly record struct FileToRead(FoundFile File, string Id, bool IsNew);

/// <summary>What a scan has to do to bring the index up to date.</summary>
/// <param name="ToRead">New, changed and returning files, in the order found.</param>
/// <param name="NowMissing">Ids of files that were there and no longer are.</param>
/// <param name="Unchanged">Files whose size and modified time are as indexed.</param>
/// <param name="NextId">The number the next new file gets.</param>
public sealed record LocalScanPlan(
    IReadOnlyList<FileToRead> ToRead, IReadOnlyList<string> NowMissing, int Unchanged, long NextId);

public static class LocalScan
{
    /// <summary>
    /// Compares a scan with the index. A file is read again only when its size or
    /// modified time changed, or it was missing and is back. A known file that
    /// wasn't found is missing only when a folder that holds it was scanned:
    /// <paramref name="scannedRoots"/> leaves out a watched folder that couldn't be
    /// listed (an unplugged drive, a share that's down), so its files stay as they
    /// were rather than all going missing at once. Files outside every watched
    /// folder are left alone as well, so a folder removed and added again gets its
    /// ids back.
    /// </summary>
    /// <param name="found">May hold a path twice (watched folders inside one
    /// another); it counts once.</param>
    /// <param name="nextId">The number the next new file gets; ids are never reused.</param>
    public static LocalScanPlan Plan(
        IEnumerable<KnownFile> known, IEnumerable<FoundFile> found,
        IReadOnlyCollection<string> scannedRoots, LocalPaths paths, long nextId)
    {
        var byPath = new Dictionary<string, KnownFile>(paths.Comparer);
        foreach (var file in known)
            byPath[file.Path] = file;

        var seen = new HashSet<string>(paths.Comparer);
        var toRead = new List<FileToRead>();
        var unchanged = 0;
        foreach (var file in found)
        {
            if (!seen.Add(file.Path))
                continue;
            if (!byPath.TryGetValue(file.Path, out var was))
                toRead.Add(new FileToRead(file, LocalIds.Format(nextId++), IsNew: true));
            else if (was.IsMissing || was.Size != file.Size || was.ModifiedAt != file.ModifiedAt)
                toRead.Add(new FileToRead(file, was.Id, IsNew: false));
            else
                unchanged++;
        }

        var nowMissing = byPath.Values
            .Where(k => !k.IsMissing && !seen.Contains(k.Path)
                && scannedRoots.Any(root => paths.IsUnder(k.Path, root)))
            .Select(k => k.Id)
            .ToList();

        return new LocalScanPlan(toRead, nowMissing, unchanged, nextId);
    }

    /// <summary>The extensions a scan looks at. Only to keep it from opening
    /// every file in the folder: the reader still goes by the content.</summary>
    public static IReadOnlySet<string> AudioExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".m4b", ".mp4", ".aac", ".alac", ".ogg", ".oga", ".opus", ".webm",
        ".mka", ".wav", ".wv", ".ape", ".aif", ".aiff", ".wma", ".dsf", ".mpc", ".tta",
    };

    /// <summary>How many files a scan reads at once: half the processor's cores,
    /// at most 4 (maintainer's decision), so the app stays responsive and a
    /// spinning disk isn't asked for many places at once.</summary>
    public static int ReadThreads(int processorCount) => Math.Clamp(processorCount / 2, 1, 4);
}

/// <summary>A file in the local index.</summary>
/// <param name="Tags">What the file's tags say, without the cover; null when the
/// file couldn't be read as audio (<see cref="IsUnreadable"/>).</param>
/// <param name="CoverPath">The cover's copy in the cover cache; null when the file
/// has none.</param>
/// <param name="IsMissing">The file was there and isn't now. It keeps its id, so
/// a queue or playlist entry shows it as missing.</param>
public sealed record LocalFile(
    string Id, string Path, long Size, DateTimeOffset ModifiedAt,
    LocalFileTags? Tags, string? CoverPath = null, bool IsMissing = false)
{
    public bool IsUnreadable => Tags is null;

    public KnownFile Known => new(Id, Path, Size, ModifiedAt, IsMissing);
}

/// <summary>Where a scan is.</summary>
/// <param name="Read">Files read so far in this scan.</param>
/// <param name="ToRead">Files this scan has to read (new, changed, back).</param>
/// <param name="UnavailableFolders">Watched folders that couldn't be listed;
/// their files keep the state they had.</param>
public sealed record LocalScanStatus(
    bool IsScanning, int Read, int ToRead, IReadOnlyList<string> UnavailableFolders)
{
    public static LocalScanStatus Idle { get; } = new(false, 0, 0, []);
}
