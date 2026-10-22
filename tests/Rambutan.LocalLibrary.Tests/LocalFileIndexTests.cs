using System.Collections.Concurrent;
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

    private LocalFileIndex Library(Func<CancellationToken, Task>? ready = null, int threads = 2) =>
        new(_data, _reader, ready ?? (_ => Task.CompletedTask), () => _folders, threads: threads);

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

        public LocalFileTags? Read(string path)
        {
            Reads.Enqueue(path);
            var text = File.ReadAllText(path);
            if (text == "bad")
                return null;
            return text.StartsWith("cover:", StringComparison.Ordinal)
                ? new LocalFileTags("mp3", Title: text["cover:".Length..],
                    Cover: new LocalCover(System.Text.Encoding.UTF8.GetBytes(text["cover:".Length..].Split(',')[0]), ".jpg"))
                : new LocalFileTags("mp3", Title: text);
        }
    }
}
