namespace Rambutan.Core.Local;

/// <summary>
/// The rules for watching the watched folders while the app runs. The watcher
/// itself is I/O (<c>LocalLibrary/LocalFolderWatcher</c>); what it does with
/// each event is decided here.
/// </summary>
public static class LocalWatch
{
    /// <summary>How long a folder must be quiet before it is scanned again: a
    /// copy of an album raises many events, and one scan after it is enough.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The folders to watch: each watched folder that isn't inside another one,
    /// since a watcher sees its subfolders. On Linux each watcher costs an
    /// inotify instance, and the system allows few (128 by default).
    /// </summary>
    public static IReadOnlyList<string> Outermost(IReadOnlyList<string> roots, LocalPaths paths) =>
        roots.Where((root, i) => !roots.Where((other, j) => j != i && !paths.Comparer.Equals(other, root))
                .Any(other => paths.IsUnder(root, other)))
            .Distinct(paths.Comparer)
            .ToList();

    /// <summary>
    /// Whether an event can change the library. The app's own folders are left
    /// out: its log and index writes would otherwise start a scan after every
    /// scan when a watched folder holds them. A changed file counts only as
    /// audio (<see cref="LocalScan.AudioExtensions"/>): a directory's change is
    /// any write inside it, a download's partial file too. A created path counts
    /// as audio or as a directory (an album copied in). A deleted or renamed path
    /// can't be asked whether it was a directory, so it counts unless its
    /// extension says it was some other file.
    /// </summary>
    /// <param name="isDirectory">For a created path: whether it is a directory.</param>
    /// <param name="ignored">The app's own folders.</param>
    public static bool Counts(
        WatcherChange change, string path, bool isDirectory, IReadOnlyList<string> ignored, LocalPaths paths)
    {
        if (ignored.Any(folder => paths.IsUnder(path, folder)))
            return false;
        var extension = Path.GetExtension(path);
        return change switch
        {
            WatcherChange.Created => isDirectory || LocalScan.AudioExtensions.Contains(extension),
            WatcherChange.Changed => !isDirectory && LocalScan.AudioExtensions.Contains(extension),
            _ => extension.Length == 0 || LocalScan.AudioExtensions.Contains(extension),
        };
    }

    /// <summary>
    /// What a watcher's error means. A full event buffer only lost events: the
    /// folder is scanned and the watcher kept. Anything else (the drive went,
    /// Linux ran out of inotify watches) stops the watcher; the folder is
    /// scanned, since its events were lost, and Settings says it isn't watched
    /// until a later scan arms it again.
    /// </summary>
    public static WatchErrorAction OnError(Exception error) =>
        error is InternalBufferOverflowException ? WatchErrorAction.ScanAndKeep : WatchErrorAction.ScanAndStop;

    /// <summary>
    /// The folders a scan covers when it replaces one still running, which it
    /// stops: both scans' folders, so the stopped one's are still scanned. Null
    /// is every watched folder.
    /// </summary>
    public static IReadOnlyList<string>? Merge(
        IReadOnlyList<string>? running, IReadOnlyList<string>? requested, LocalPaths paths) =>
        running is null || requested is null ? null : running.Concat(requested).Distinct(paths.Comparer).ToList();

    /// <summary>The watched folders a scan of <paramref name="scope"/> lists: those
    /// inside one of its folders (a folder is inside itself). A folder no longer
    /// watched isn't scanned.</summary>
    public static IReadOnlyList<string> Within(
        IReadOnlyList<string> roots, IReadOnlyList<string>? scope, LocalPaths paths) =>
        scope is null ? roots : roots.Where(root => scope.Any(s => paths.IsUnder(root, s))).ToList();

    /// <summary>
    /// The unavailable folders after a scan of some of them: the ones it couldn't
    /// list, and the earlier ones it didn't look at, if they are still watched.
    /// A scan of one folder says nothing about a drive it never touched.
    /// </summary>
    public static IReadOnlyList<string> Unavailable(
        IReadOnlyList<string> before, IReadOnlyList<string> looked, IReadOnlyList<string> unavailable,
        IReadOnlyList<string> roots, LocalPaths paths) =>
        before.Where(f => !looked.Contains(f, paths.Comparer) && roots.Contains(f, paths.Comparer))
            .Concat(unavailable)
            .Distinct(paths.Comparer)
            .ToList();
}

/// <summary>A watcher event, as far as the rules care.</summary>
public enum WatcherChange
{
    Created,
    Changed,
    DeletedOrRenamed,
}

public enum WatchErrorAction
{
    ScanAndKeep,
    ScanAndStop,
}

/// <summary>
/// Holds each folder's changes until it has been quiet for
/// <see cref="LocalWatch.QuietPeriod"/>. Not thread-safe: the caller locks.
/// </summary>
public sealed class FolderChangeDebounce(TimeSpan quiet, IEqualityComparer<string> comparer)
{
    private readonly Dictionary<string, DateTimeOffset> _last = new(comparer);

    public void Changed(string folder, DateTimeOffset now) => _last[folder] = now;

    /// <summary>When the next folder is due; null when none waits.</summary>
    public DateTimeOffset? NextDue => _last.Count == 0 ? null : _last.Values.Min() + quiet;

    /// <summary>The folders quiet since <paramref name="now"/> minus the quiet
    /// period, which stop waiting.</summary>
    public IReadOnlyList<string> TakeDue(DateTimeOffset now)
    {
        var due = _last.Where(p => now - p.Value >= quiet).Select(p => p.Key).ToList();
        foreach (var folder in due)
            _last.Remove(folder);
        return due;
    }
}
