using Rambutan.Core.Local;

namespace Rambutan.Core.Tests;

public class LocalWatchTests
{
    private static readonly LocalPaths Linux = new(ignoreCase: false, '/');
    private static readonly LocalPaths Windows = new(ignoreCase: true, '\\', '/');

    [Fact]
    public void Outermost_WatchesAFolderInsideAnotherWithIt()
    {
        Assert.Equal(["/m", "/n"], LocalWatch.Outermost(["/m", "/m/rock", "/n", "/m"], Linux));
        Assert.Equal(["/m", "/music"], LocalWatch.Outermost(["/m", "/music"], Linux)); // a prefix isn't a parent
        Assert.Equal([@"C:\Music"], LocalWatch.Outermost([@"C:\Music", @"c:\music\Rock"], Windows));
    }

    [Theory]
    [InlineData(WatcherChange.Created, "/m/a/song.flac", false, true)]
    [InlineData(WatcherChange.Created, "/m/New Album", true, true)]
    [InlineData(WatcherChange.Created, "/m/a/back.jpg", false, false)]
    [InlineData(WatcherChange.Changed, "/m/a/song.MP3", false, true)]
    [InlineData(WatcherChange.Changed, "/m/a", true, false)] // any write inside it
    [InlineData(WatcherChange.Changed, "/m/a/song.flac.part", false, false)]
    [InlineData(WatcherChange.DeletedOrRenamed, "/m/a/song.opus", false, true)]
    [InlineData(WatcherChange.DeletedOrRenamed, "/m/Old Album", false, true)] // may have been a folder
    [InlineData(WatcherChange.DeletedOrRenamed, "/m/a/notes.txt", false, false)]
    [InlineData(WatcherChange.Created, "/m/rambutan/logs/app.mp3", false, false)] // the app's own folder
    public void Counts(WatcherChange change, string path, bool isDirectory, bool expected) =>
        Assert.Equal(expected, LocalWatch.Counts(change, path, isDirectory, ["/m/rambutan"], Linux));

    [Fact]
    public void AFullBuffer_KeepsTheWatcher_AnyOtherErrorStopsIt()
    {
        Assert.Equal(WatchErrorAction.ScanAndKeep, LocalWatch.OnError(new InternalBufferOverflowException()));
        Assert.Equal(WatchErrorAction.ScanAndStop, LocalWatch.OnError(new IOException("inotify limit")));
    }

    [Fact]
    public void Merge_KeepsTheStoppedScansFolders()
    {
        Assert.Null(LocalWatch.Merge(null, ["/m"], Linux));
        Assert.Null(LocalWatch.Merge(["/m"], null, Linux));
        Assert.Equal(["/m", "/n"], LocalWatch.Merge(["/m"], ["/n", "/m"], Linux));
    }

    [Fact]
    public void Within_TakesTheWatchedFoldersInsideTheScope()
    {
        string[] roots = ["/m", "/m/rock", "/n"];

        Assert.Equal(roots, LocalWatch.Within(roots, null, Linux));
        Assert.Equal(["/m", "/m/rock"], LocalWatch.Within(roots, ["/m"], Linux));
        Assert.Empty(LocalWatch.Within(roots, ["/gone"], Linux));
    }

    [Fact]
    public void Unavailable_KeepsWhatTheScanDidntLookAt()
    {
        string[] roots = ["/m", "/usb", "/nas"];

        // /m scanned and fine; /usb not looked at, still unavailable; /old no longer watched.
        Assert.Equal(["/usb"], LocalWatch.Unavailable(["/usb", "/old"], ["/m"], [], roots, Linux));
        // /usb looked at and back.
        Assert.Empty(LocalWatch.Unavailable(["/usb"], ["/usb"], [], roots, Linux));
        Assert.Equal(["/usb", "/nas"], LocalWatch.Unavailable(["/usb"], ["/nas"], ["/nas"], roots, Linux));
    }

    [Fact]
    public void TheDebounce_WaitsForAFolderToBeQuiet()
    {
        var start = DateTimeOffset.UnixEpoch;
        var quiet = TimeSpan.FromSeconds(2);
        var debounce = new FolderChangeDebounce(quiet, StringComparer.Ordinal);
        Assert.Null(debounce.NextDue);

        debounce.Changed("/m", start);
        debounce.Changed("/n", start.AddSeconds(1));
        debounce.Changed("/m", start.AddSeconds(1.5)); // still copying

        Assert.Equal(start.AddSeconds(3), debounce.NextDue);
        Assert.Empty(debounce.TakeDue(start.AddSeconds(2.9)));
        Assert.Equal(["/n"], debounce.TakeDue(start.AddSeconds(3)));
        Assert.Equal(["/m"], debounce.TakeDue(start.AddSeconds(3.5)));
        Assert.Null(debounce.NextDue);
    }
}
