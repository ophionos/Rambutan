using System.Collections.Concurrent;
using System.Diagnostics;
using Rambutan.Core.Local;

namespace Rambutan.LocalLibrary.Tests;

public sealed class LocalFileIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rambutan-local-{Guid.NewGuid():N}");
    private readonly string _data;
    private readonly string _music;
    private readonly FakeReader _reader = new();
    private readonly List<string> _folders = [];

    public LocalFileIndexTests()
    {
        _data = Path.Combine(_root, "data");
        _music = Path.Combine(_root, "music");
        Directory.CreateDirectory(_music);
        _folders.Add(_music);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private LocalFileIndex Library(
        Func<CancellationToken, Task>? ready = null, int threads = 2, IReadOnlySet<string>? queued = null) =>
        new(_data, _reader, ready ?? (_ => Task.CompletedTask), () => _folders, threads: threads,
            queued: queued is null ? null : () => queued);

    /// <summary>The fake reader takes a file's text as its title; "bad" isn't audio,
    /// and "cover:X" carries a cover of the bytes of X.</summary>
    private string Write(string relative, string content = "song")
    {
        var path = Path.Combine(_music, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static LocalFile ByPath(LocalFileIndex library, string path) =>
        library.Files().Single(f => f.Path == path);

    [Fact]
    public async Task AScan_ReadsTheAudioFiles_AndGivesEachAnId()
    {
        var a = Write("a.mp3", "A");
        var b = Write(Path.Combine("Album", "b.FLAC"), "B");
        Write("notes.txt", "not audio");
        using var library = Library();

        await library.ScanAsync();

        Assert.Equal(2, library.Files().Count);
        Assert.Equal("A", ByPath(library, a).Tags?.Title);
        Assert.Equal("B", ByPath(library, b).Tags?.Title);
        Assert.Equal(["local:1", "local:2"], library.Files().Select(f => f.Id).Order());
        Assert.False(library.Status.IsScanning);
        Assert.Equal(2, library.Status.Read);
    }

    [Fact]
    public async Task ARescan_ReadsOnlyWhatChanged()
    {
        var a = Write("a.mp3", "A");
        Write("b.mp3", "B");
        using var library = Library();
        await library.ScanAsync();
        var id = ByPath(library, a).Id;
        _reader.Reads.Clear();

        File.WriteAllText(a, "A, longer");
        await library.ScanAsync();

        Assert.Equal([a], _reader.Reads);
        Assert.Equal(id, ByPath(library, a).Id);
        Assert.Equal("A, longer", ByPath(library, a).Tags?.Title);
    }

    [Fact]
    public async Task ADeletedFile_IsMissing_AndKeepsItsIdWhenItComesBack()
    {
        var a = Write("a.mp3", "A");
        Write("b.mp3", "B");
        using var library = Library();
        await library.ScanAsync();
        var id = ByPath(library, a).Id;

        File.Delete(a);
        await library.ScanAsync();
        var missing = ByPath(library, a);
        Write("a.mp3", "A again");
        await library.ScanAsync();

        Assert.True(missing.IsMissing);
        Assert.Equal(id, missing.Id);
        Assert.False(ByPath(library, a).IsMissing);
        Assert.Equal(id, ByPath(library, a).Id);
    }

    [Fact]
    public async Task AFolderThatIsNotThere_LeavesItsFilesAsTheyWere()
    {
        var usb = Path.Combine(_root, "usb");
        Directory.CreateDirectory(usb);
        var song = Path.Combine(usb, "a.mp3");
        File.WriteAllText(song, "A");
        _folders.Add(usb);
        using var library = Library();
        await library.ScanAsync();

        Directory.Delete(usb, recursive: true);
        await library.ScanAsync();

        Assert.False(ByPath(library, song).IsMissing);
        Assert.Equal([usb], library.Status.UnavailableFolders);
    }

    [Fact]
    public async Task AFolderThatListsEmpty_LeavesItsFilesAsTheyWere_UntilOneComesBack()
    {
        // On Linux, a mount whose drive is down is an empty folder.
        var a = Write("a.mp3", "A");
        var b = Write(Path.Combine("x", "b.mp3"), "B");
        using var library = Library();
        await library.ScanAsync();

        File.Delete(a);
        File.Delete(b);
        await library.ScanAsync();
        Assert.False(ByPath(library, a).IsMissing);
        Assert.False(ByPath(library, b).IsMissing);
        Assert.Equal([_music], library.Status.UnavailableFolders);

        Write("a.mp3", "A");
        await library.ScanAsync();
        Assert.False(ByPath(library, a).IsMissing);
        Assert.True(ByPath(library, b).IsMissing);
        Assert.Empty(library.Status.UnavailableFolders);
    }

    [Fact]
    public async Task Counts_SplitTheListedFiles()
    {
        Write("a.mp3", "A");
        Write("bad.mp3", "bad");
        var gone = Write("gone.mp3", "G");
        using var library = Library();
        await library.ScanAsync();
        File.Delete(gone);
        await library.ScanAsync();

        Assert.Equal((1, 1, 1), library.Counts());
    }

    [Fact]
    public async Task AnUnreadableFile_IsKept_AndNotReadAgainUntilItChanges()
    {
        var bad = Write("bad.mp3", "bad");
        using var library = Library();
        await library.ScanAsync();
        _reader.Reads.Clear();

        await library.ScanAsync();

        Assert.True(ByPath(library, bad).IsUnreadable);
        Assert.Empty(_reader.Reads);
    }

    [Fact]
    public async Task TheIndex_IsKeptAcrossStarts_AndIdsAreNotGivenTwice()
    {
        var a = Write("a.mp3", "A");
        string id;
        using (var first = Library())
        {
            await first.ScanAsync();
            id = ByPath(first, a).Id;
        }

        Write("b.mp3", "B");
        using var second = Library();
        Assert.Equal("A", ByPath(second, a).Tags?.Title);
        _reader.Reads.Clear();
        await second.ScanAsync();

        Assert.Equal(id, ByPath(second, a).Id);
        Assert.Equal("local:2", second.Files().Single(f => f.Tags?.Title == "B").Id);
        Assert.DoesNotContain(a, _reader.Reads);
    }

    [Fact]
    public async Task TheYouTubeIdsOfAFile_AreKeptAcrossStarts()
    {
        var a = Write("a.mp3", "ids:YQHsXMglC9A,khnokW3Mw24");
        using (var first = Library())
            await first.ScanAsync();

        using var second = Library();
        var tags = ByPath(second, a).Tags;

        Assert.Equal("YQHsXMglC9A", tags?.YouTubeVideoId);
        Assert.Equal("khnokW3Mw24", tags?.YouTubeSongId);
    }

    private static void RecordDownload(LocalFileIndex library, string path, string videoId)
    {
        var info = new FileInfo(path);
        library.RecordDownload(path, info.Length, info.LastWriteTimeUtc,
            new LocalDownload(new Rambutan.Core.Models.TrackInfo { VideoId = videoId, Title = "Downloaded", Artists = [] },
                "opus", null, null, null, info.Length, DateTimeOffset.UtcNow), []);
    }

    [Fact]
    public async Task AnExportedCopy_IsListedOnce_AsItsDownload_UntilTheDownloadIsDeleted()
    {
        var download = Write(Path.Combine("downloads", "YQHsXMglC9A.opus"), "Downloaded");
        var copy = Write(Path.Combine("exports", "Hello.mp3"), "ids:YQHsXMglC9A,khnokW3Mw24");
        var other = Write(Path.Combine("exports", "Other.mp3"), "ids:khnokW3Mw24,khnokW3Mw24");
        using var library = Library();
        RecordDownload(library, download, "YQHsXMglC9A");
        await library.ScanAsync();

        Assert.Equal([download, other], library.Files().Select(f => f.Path).Order());
        Assert.Equal((2, 0, 0), library.Counts());

        var raised = 0;
        library.Changed += () => raised++;
        Assert.True(library.RemoveDownload("YQHsXMglC9A", []));

        Assert.Contains(copy, library.Files().Select(f => f.Path));
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task AnExportedCopy_IsListed_WhenItsDownloadIsNot()
    {
        // The download is gone, or outside the watched folders.
        var download = Write(Path.Combine("downloads", "YQHsXMglC9A.opus"), "Downloaded");
        var copy = Write(Path.Combine("exports", "Hello.mp3"), "ids:YQHsXMglC9A,khnokW3Mw24");
        using var library = Library();
        RecordDownload(library, download, "YQHsXMglC9A");
        await library.ScanAsync();
        File.Delete(download);
        await library.ScanAsync();

        Assert.Contains(copy, library.Files().Select(f => f.Path));

        _folders[0] = Path.Combine(_music, "exports");

        Assert.Equal([copy], library.Files().Select(f => f.Path));
    }

    [Fact]
    public async Task ADamagedIndex_IsSetAside_AndNewIdsCannotMeetItsOldOnes()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(Path.Combine(_data, "local-index.json"), "{ not json");
        Write("a.mp3", "A");
        using var library = Library();

        await library.ScanAsync();

        Assert.True(File.Exists(Path.Combine(_data, "local-index.json.unreadable")));
        var number = long.Parse(library.Files().Single().Id["local:".Length..]);
        Assert.True(number > 1_000_000_000);
    }

    [Fact]
    public async Task AnIndexFromANewerBuild_IsSetAside()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(Path.Combine(_data, "local-index.json"), """{"version":99,"nextId":5,"files":[]}""");
        using var library = Library();

        await library.ScanAsync();

        Assert.True(File.Exists(Path.Combine(_data, "local-index.json.unreadable")));
    }

    [Fact]
    public async Task Covers_AreKeptOncePerPicture_AndRemovedWhenUnused()
    {
        var a = Write("a.mp3", "cover:red");
        var b = Write("b.mp3", "cover:red");
        using var library = Library();
        await library.ScanAsync();
        var cover = ByPath(library, a).CoverPath;

        Assert.NotNull(cover);
        Assert.Equal(cover, ByPath(library, b).CoverPath);
        Assert.Equal("red", File.ReadAllText(cover));
        Assert.Single(Directory.GetFiles(Path.Combine(_data, LocalFileIndex.CoverFolderName)));

        File.WriteAllText(a, "cover:blue, now");
        File.WriteAllText(b, "no cover now");
        await library.ScanAsync();

        Assert.False(File.Exists(cover));
        Assert.Equal("blue", File.ReadAllText(ByPath(library, a).CoverPath!));
    }

    [Fact]
    public async Task AnAlbumsTracks_ReadAtOnce_AllGetTheirCover()
    {
        // Several threads write the same picture under the same name.
        for (var i = 0; i < 40; i++)
            Write($"{i:D2}.mp3", "cover:red");
        using var library = Library(threads: 4);

        await library.ScanAsync();

        Assert.All(library.Files(), f => Assert.NotNull(f.CoverPath));
        Assert.Single(Directory.GetFiles(Path.Combine(_data, LocalFileIndex.CoverFolderName)));
    }

    [Fact]
    public async Task ACoverPath_ResolvesAfterARestart()
    {
        var a = Write("a.mp3", "cover:red");
        using (var first = Library())
            await first.ScanAsync();

        using var second = Library();

        Assert.Equal("red", File.ReadAllText(ByPath(second, a).CoverPath!));
    }

    [Fact]
    public async Task AFolderNoLongerWatched_IsNotListed_AndGetsItsIdsBack()
    {
        var a = Write("a.mp3", "A");
        using var library = Library();
        await library.ScanAsync();
        var id = ByPath(library, a).Id;

        _folders.Clear();
        await library.ScanAsync();
        Assert.Empty(library.Files());
        Assert.Equal(a, library.Find(id)?.Path);

        _folders.Add(_music);
        await library.ScanAsync();
        Assert.Equal(id, ByPath(library, a).Id);
    }

    [Fact]
    public async Task FoldersInsideOneAnother_ListAFileOnce()
    {
        Write(Path.Combine("Album", "a.mp3"), "A");
        _folders.Add(Path.Combine(_music, "Album") + Path.DirectorySeparatorChar);
        using var library = Library();

        await library.ScanAsync();

        Assert.Single(library.Files());
    }

    [Fact]
    public async Task AScan_WaitsForTheReader()
    {
        Write("a.mp3", "A");
        var ready = new TaskCompletionSource();
        using var library = Library(_ => ready.Task);

        var scan = library.ScanAsync();
        await Task.Delay(50);
        Assert.False(scan.IsCompleted);
        Assert.True(library.Status.IsScanning);
        Assert.Empty(_reader.Reads);

        ready.SetResult();
        await scan;
        Assert.Single(library.Files());
    }

    [Fact]
    public async Task ANewScan_StopsTheOneRunning()
    {
        Write("a.mp3", "A");
        var ready = new TaskCompletionSource();
        using var library = Library(ct => ready.Task.WaitAsync(ct));

        var first = library.ScanAsync();
        var second = library.ScanAsync();
        ready.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await second;
        Assert.Single(library.Files());
    }

    [Fact]
    public async Task AScanOfOneFolder_LeavesTheOthersAsTheyWere()
    {
        var other = Path.Combine(_root, "other");
        var usb = Path.Combine(_root, "usb");
        Directory.CreateDirectory(other);
        var song = Path.Combine(other, "b.mp3");
        File.WriteAllText(song, "B");
        File.WriteAllText(Path.Combine(other, "d.mp3"), "D");
        _folders.Add(other);
        _folders.Add(usb); // not there: unavailable
        Write("a.mp3", "A");
        using var library = Library();
        await library.ScanAsync();
        Assert.Equal([usb], library.Status.UnavailableFolders);

        File.Delete(song);
        var added = Write("c.mp3", "C");
        _reader.Reads.Clear();
        await library.ScanAsync([_music]);

        Assert.Equal([added], _reader.Reads);
        Assert.False(ByPath(library, song).IsMissing); // its folder wasn't looked at
        Assert.Equal([usb], library.Status.UnavailableFolders);

        await library.ScanAsync([other]);
        Assert.True(ByPath(library, song).IsMissing);
    }

    [Fact]
    public async Task AFolderScan_ThatStopsAFullOne_ScansEveryFolder()
    {
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        var song = Path.Combine(other, "b.mp3");
        File.WriteAllText(song, "B");
        _folders.Add(other);
        Write("a.mp3", "A");
        var ready = new TaskCompletionSource();
        using var library = Library(ct => ready.Task.WaitAsync(ct));

        var full = library.ScanAsync();
        var folder = library.ScanAsync([_music]);
        ready.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => full);
        await folder;
        Assert.Equal(2, library.Files().Count);
    }

    [Fact]
    public async Task AScanOfAFolderNoLongerWatched_ScansNothing()
    {
        Write("a.mp3", "A");
        using var library = Library();

        await library.ScanAsync([Path.Combine(_root, "gone")]);

        Assert.Empty(library.Files());
    }

    [Fact]
    public async Task AFolderPicture_IsTheCover_OfTracksThatEmbedNone()
    {
        var plain = Write(Path.Combine("Album", "a.mp3"), "A");
        var embedded = Write(Path.Combine("Album", "b.mp3"), "cover:own");
        var picture = Write(Path.Combine("Album", "cover.jpg"), "folder picture");
        using var library = Library();

        await library.ScanAsync();

        var a = ByPath(library, plain);
        Assert.Equal("folder picture", File.ReadAllText(a.CoverPath!));
        Assert.Equal(picture, a.FolderCover?.Path);
        Assert.Equal("own", File.ReadAllText(ByPath(library, embedded).CoverPath!)); // embedded wins
        Assert.Null(ByPath(library, embedded).FolderCover);
    }

    [Fact]
    public async Task AChangedOrRemovedFolderPicture_IsNoticed_WithoutReadingTheTracks()
    {
        var song = Write(Path.Combine("Album", "a.mp3"), "A");
        var picture = Write(Path.Combine("Album", "folder.jpg"), "old");
        using var library = Library();
        await library.ScanAsync();
        _reader.Reads.Clear();

        File.WriteAllText(picture, "newer picture");
        await library.ScanAsync();
        Assert.Equal("newer picture", File.ReadAllText(ByPath(library, song).CoverPath!));
        Assert.Empty(_reader.Reads);

        File.Delete(picture);
        await library.ScanAsync();
        Assert.Null(ByPath(library, song).CoverPath);
        Assert.Null(ByPath(library, song).FolderCover);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_data, LocalFileIndex.CoverFolderName)));
    }

    [Fact]
    public async Task AFolderPicture_IsKeptAcrossStarts()
    {
        var song = Write(Path.Combine("Album", "a.mp3"), "A");
        var picture = Write(Path.Combine("Album", "cover.png"), "picture");
        using (var first = Library())
            await first.ScanAsync();

        using var library = Library();
        Assert.Equal(picture, ByPath(library, song).FolderCover?.Path);
        _reader.Reads.Clear();
        await library.ScanAsync();
        Assert.Empty(_reader.Reads);
        Assert.Equal("picture", File.ReadAllText(ByPath(library, song).CoverPath!));
    }

    [Fact]
    public async Task AHiddenFolderPicture_IsFound_ButHiddenAudioIsNot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Hidden is an attribute on Windows only.");
        var song = Write(Path.Combine("Album", "a.mp3"), "A");
        var hiddenSong = Write(Path.Combine("Album", "b.mp3"), "B");
        var picture = Write(Path.Combine("Album", "Folder.jpg"), "picture");
        File.SetAttributes(picture, FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(hiddenSong, FileAttributes.Hidden);
        using var library = Library();

        await library.ScanAsync();

        Assert.Equal(picture, ByPath(library, song).FolderCover?.Path);
        Assert.DoesNotContain(library.Files(), f => f.Path == hiddenSong);
        File.SetAttributes(picture, FileAttributes.Normal);
    }

    private string WriteElsewhere(string relative, string content)
    {
        var path = Path.Combine(_root, "elsewhere", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task AnOpenedFile_OutsideTheWatchedFolders_GetsAnId_ButIsNotListed()
    {
        var song = WriteElsewhere("a.mp3", "A");
        using var library = Library();

        var opened = Assert.Single(await library.OpenAsync([song]));

        Assert.True(LocalIds.IsLocal(opened.Id));
        Assert.True(opened.IsOpened);
        Assert.Equal("A", opened.Tags?.Title);
        Assert.Same(opened, library.Find(opened.Id));
        Assert.Empty(library.Files());
    }

    [Fact]
    public async Task AnOpenedFile_InAWatchedFolder_KeepsItsId_AndIsNotReadAgain()
    {
        var song = Write("a.mp3", "A");
        using var library = Library();
        await library.ScanAsync();
        var id = ByPath(library, song).Id;
        _reader.Reads.Clear();

        var opened = Assert.Single(await library.OpenAsync([song]));

        Assert.Equal(id, opened.Id);
        Assert.False(opened.IsOpened);
        Assert.Empty(_reader.Reads);
    }

    [Fact]
    public async Task AnOpenedFile_IsReadAgain_WhenItChanged()
    {
        var song = WriteElsewhere("a.mp3", "A");
        using var library = Library();
        var id = Assert.Single(await library.OpenAsync([song])).Id;

        File.WriteAllText(song, "A, longer");
        File.SetLastWriteTimeUtc(song, DateTime.UtcNow.AddMinutes(1));
        var again = Assert.Single(await library.OpenAsync([song]));

        Assert.Equal(id, again.Id);
        Assert.Equal("A, longer", again.Tags?.Title);
        Assert.True(again.IsOpened);
    }

    [Fact]
    public async Task AnOpenedFolder_GivesItsAudioFiles_SubfoldersIncluded_InPathOrder()
    {
        var b = WriteElsewhere(Path.Combine("Album", "b.mp3"), "B");
        var a = WriteElsewhere(Path.Combine("Album", "a.flac"), "A");
        var inner = WriteElsewhere(Path.Combine("Album", "CD2", "c.mp3"), "C");
        var upper = WriteElsewhere(Path.Combine("Album", "Zed.mp3"), "Z");
        WriteElsewhere(Path.Combine("Album", "notes.txt"), "notes");
        var single = WriteElsewhere("single.mp3", "S");
        using var library = Library();

        var opened = await library.OpenAsync([single, Path.GetDirectoryName(a)!, b]);

        Assert.Equal([single, a, b, inner, upper], opened.Select(f => f.Path));
    }

    [Fact]
    public async Task AnOpen_SaysHowManyFilesItFound_BeforeReadingThem()
    {
        var a = WriteElsewhere(Path.Combine("Album", "a.mp3"), "A");
        WriteElsewhere(Path.Combine("Album", "b.mp3"), "bad");
        using var library = Library();
        var told = new List<(int Count, int ReadBefore)>();

        await library.OpenAsync([Path.GetDirectoryName(a)!], count => told.Add((count, _reader.Reads.Count)));
        await library.OpenAsync([Path.Combine(_root, "gone.mp3")], count => told.Add((count, -1)));

        Assert.Equal([(2, 0)], told);
    }

    [Fact]
    public async Task AnOpenedFile_ThatIsNotAudio_OrNotThere_IsLeftOut_AndNotKept()
    {
        var bad = WriteElsewhere("bad.mp3", "bad");
        var song = WriteElsewhere("a.mp3", "A");
        using var library = Library();

        var opened = await library.OpenAsync([bad, Path.Combine(_root, "gone.mp3"), song]);

        Assert.Equal([song], opened.Select(f => f.Path));
        Assert.Null(library.Find(LocalIds.Format(1)));
    }

    [Fact]
    public async Task OpenedFiles_AreKeptAcrossStarts_UntilNoQueueHoldsThem()
    {
        var kept = WriteElsewhere("kept.mp3", "K");
        var dropped = WriteElsewhere("dropped.mp3", "D");
        string keptId, droppedId;
        using (var first = Library())
        {
            var opened = await first.OpenAsync([kept, dropped]);
            (keptId, droppedId) = (opened[0].Id, opened[1].Id);
        }

        using (var library = Library())
            Assert.NotNull(library.Find(droppedId));
        using (var library = Library(queued: new HashSet<string> { keptId }))
        {
            Assert.Equal(kept, library.Find(keptId)?.Path);
            Assert.Null(library.Find(droppedId));
        }

        using var restarted = Library();
        Assert.Null(restarted.Find(droppedId));
        Assert.NotEqual(droppedId, Assert.Single(await restarted.OpenAsync([dropped])).Id);
    }

    [Fact]
    public async Task AnOpenedFile_InAFolderWatchedSince_IsNotForgotten()
    {
        var song = WriteElsewhere("a.mp3", "A");
        string id;
        using (var first = Library())
            id = Assert.Single(await first.OpenAsync([song])).Id;
        _folders.Add(Path.GetDirectoryName(song)!);

        using var library = Library(queued: new HashSet<string>());

        Assert.False(library.Find(id)!.IsOpened);
        Assert.Equal([id], library.Files().Select(f => f.Id));
    }

    [Fact]
    public async Task AnOpenedFile_ThatAScanIsReading_GetsTheScansId()
    {
        var song = Write("a.mp3", "A");
        using var library = Library();
        _reader.Hold = new ManualResetEventSlim(false);

        var scan = library.ScanAsync();
        var waited = Stopwatch.StartNew();
        while (!_reader.Reads.Contains(song) && waited.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10);
        var open = library.OpenAsync([song]);
        // A second read means the open found no entry for the path and went on
        // with the id the scan holds for it.
        while (_reader.Reads.Count(r => r == song) < 2 && waited.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10);
        Assert.Equal(2, _reader.Reads.Count(r => r == song));
        _reader.Hold.Set();
        var opened = Assert.Single(await open);
        await scan;

        Assert.Equal(ByPath(library, song).Id, opened.Id);
        Assert.Single(library.Files());
    }

    [Fact]
    public async Task Changed_IsRaised_AsTheScanGoes()
    {
        Write("a.mp3", "A");
        using var library = Library();
        var raised = 0;
        library.Changed += () => Interlocked.Increment(ref raised);

        await library.ScanAsync();

        Assert.True(raised >= 2);
    }

    private sealed class FakeReader : IMediaFileReader
    {
        public ConcurrentQueue<string> Reads { get; } = new();

        /// <summary>When set, a read waits for it.</summary>
        public ManualResetEventSlim? Hold { get; set; }

        public LocalFileTags? Read(string path)
        {
            Reads.Enqueue(path);
            Hold?.Wait(TimeSpan.FromSeconds(10));
            var text = File.ReadAllText(path);
            if (text == "bad")
                return null;
            if (text.StartsWith("ids:", StringComparison.Ordinal))
                return new LocalFileTags("mp3", Title: text,
                    YouTubeVideoId: text[4..].Split(',')[0], YouTubeSongId: text[4..].Split(',')[1]);
            return text.StartsWith("cover:", StringComparison.Ordinal)
                ? new LocalFileTags("mp3", Title: text["cover:".Length..],
                    Cover: new LocalCover(System.Text.Encoding.UTF8.GetBytes(text["cover:".Length..].Split(',')[0]), ".jpg"))
                : new LocalFileTags("mp3", Title: text);
        }
    }
}
