using System.Text;
using Rambutan.Core.Local;
using Rambutan.Core.Playlists;

namespace Rambutan.LocalLibrary.Tests;

public sealed class M3uPlaylistFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rambutan-m3u-{Guid.NewGuid():N}");
    private readonly string _data;
    private readonly string _music;
    private readonly string _elsewhere;
    private readonly string _lists;
    private readonly List<string> _folders = [];

    public M3uPlaylistFilesTests()
    {
        _data = Path.Combine(_root, "data");
        _music = Path.Combine(_root, "music");
        _elsewhere = Path.Combine(_root, "elsewhere");
        _lists = Path.Combine(_root, "lists");
        foreach (var dir in new[] { _music, _elsewhere, _lists })
            Directory.CreateDirectory(dir);
        _folders.Add(_music);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A file's text is its title; "bad" isn't audio.</summary>
    private sealed class TitleReader : IMediaFileReader
    {
        public LocalFileTags? Read(string path) =>
            File.ReadAllText(path) is var text && text == "bad" ? null : new LocalFileTags("mp3", Title: text);
    }

    private LocalFileIndex Index(IReadOnlySet<string>? queued = null) =>
        new(_data, new TitleReader(), _ => Task.CompletedTask, () => _folders, threads: 2,
            queued: queued is null ? null : () => queued);

    private static string Write(string folder, string name, string content)
    {
        var path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string List(string text, string name = "Evening.m3u8") => Write(_lists, name, text);

    [Fact]
    public async Task AnImport_GivesEveryEntryItsRow_InOrder_DuplicatesIncluded()
    {
        var scanned = Write(_music, "a.mp3", "A");
        var opened = Write(_elsewhere, "b.mp3", "B");
        using var index = Index();
        await index.ScanAsync();
        var scannedId = index.Files().Single().Id;
        var list = List($"#EXTM3U\n../music/a.mp3\n{opened}\nhttps://example.com/x.mp3\n../music/a.mp3\n");

        var read = await M3uPlaylistFiles.ReadAsync(index, list);

        Assert.Equal("Evening", read.Name);
        Assert.Equal([scanned, opened, scanned], read.Items.Select(i => i.Path));
        Assert.Equal(scannedId, read.Items[0].Track.VideoId);
        Assert.Equal(["A", "B", "A"], read.Items.Select(i => i.Track.Title));
        Assert.Equal(1, read.Skipped);
        Assert.True(index.Find(read.Items[1].Track.VideoId)!.IsOpened);
        Assert.Single(index.Files());
    }

    [Fact]
    public async Task AFileThatIsNotAudio_OrAFolder_IsSkipped_NotMissing()
    {
        Write(_elsewhere, "bad.mp3", "bad");
        Write(_elsewhere, Path.Combine("Album", "c.mp3"), "C");
        using var index = Index();
        var list = List("../elsewhere/bad.mp3\n../elsewhere/Album\n");

        var read = await M3uPlaylistFiles.ReadAsync(index, list);

        Assert.Empty(read.Items);
        Assert.Equal(2, read.Skipped);
    }

    [Fact]
    public async Task AMissingPath_InAWatchedFolder_IsRemembered_Unlisted_AndFoundByTheScanThatSeesIt()
    {
        var path = Path.Combine(_music, "later.mp3");
        using var index = Index();
        await index.ScanAsync();

        var read = await M3uPlaylistFiles.ReadAsync(index, List("../music/later.mp3\n"));

        var row = Assert.Single(read.Items);
        Assert.Equal(path, row.Path);
        var entry = index.Find(row.Track.VideoId)!;
        Assert.True(entry.IsNeverSeen);
        Assert.Empty(index.Files());
        Assert.Equal((0, 0, 0), index.Counts());

        Write(_music, "later.mp3", "Later");
        await index.ScanAsync();

        var found = Assert.Single(index.Files());
        Assert.Equal(row.Track.VideoId, found.Id);
        Assert.Equal("Later", found.Tags?.Title);
        Assert.False(found.IsMissing);
    }

    [Fact]
    public async Task AMissingPath_Elsewhere_IsFoundWhenThatFileIsOpened()
    {
        using var index = Index();
        var read = await M3uPlaylistFiles.ReadAsync(index, List("../elsewhere/later.mp3\n"));
        var id = Assert.Single(read.Items).Track.VideoId;

        var path = Write(_elsewhere, "later.mp3", "Later");
        var opened = Assert.Single(await index.OpenAsync([path]));

        Assert.Equal(id, opened.Id);
        Assert.Equal("Later", opened.Tags?.Title);
    }

    [Fact]
    public async Task ARememberedPath_StaysWhileAPlaylistHoldsIt_AndGoesWhenNoneDoes()
    {
        string held, dropped;
        using (var index = Index())
        {
            var read = await M3uPlaylistFiles.ReadAsync(index, List("../music/one.mp3\n../music/two.mp3\n"));
            (held, dropped) = (read.Items[0].Track.VideoId, read.Items[1].Track.VideoId);
        }

        using var again = Index(queued: new HashSet<string> { held });

        Assert.True(again.Find(held)!.IsNeverSeen);
        Assert.Null(again.Find(dropped));
        Assert.Empty(again.Files());
    }

    [Fact]
    public async Task APlainM3u_InALegacyCodePage_ReadsItsNames()
    {
        var name = "Café.mp3";
        Write(_lists, name, "C");
        using var index = Index();
        var list = Path.Combine(_lists, "old.m3u");
        File.WriteAllBytes(list, Encoding.Latin1.GetBytes(name + "\n"));

        var read = await M3uPlaylistFiles.ReadAsync(index, list);

        Assert.Equal("C", Assert.Single(read.Items).Track.Title);
    }

    [Fact]
    public async Task AnExport_ReadsBackAsTheSameRows()
    {
        var a = Write(_music, "a.mp3", "A");
        var b = Write(_elsewhere, "b.mp3", "B");
        using var index = Index();
        var first = await M3uPlaylistFiles.ReadAsync(index, List($"{a}\n{b}\n"));
        var song = LocalPlaylistItem.Of(new Core.Models.TrackInfo { VideoId = "dQw4w9WgXcQ", Title = "Song", Artists = [] });
        var exported = Path.Combine(_lists, "Out.m3u8");

        var (written, leftOut) = await M3uPlaylistFiles.WriteAsync([first.Items[0], song, first.Items[1]], exported);
        var again = await M3uPlaylistFiles.ReadAsync(index, exported);

        Assert.Equal((2, 1), (written, leftOut));
        Assert.Contains($"..{Path.DirectorySeparatorChar}music{Path.DirectorySeparatorChar}a.mp3", File.ReadAllText(exported));
        Assert.Equal(first.Items.Select(i => i.Track.VideoId), again.Items.Select(i => i.Track.VideoId));
        Assert.NotEqual(0xEF, File.ReadAllBytes(exported)[0]);
    }

    [Fact]
    public async Task AFileTooLargeForAPlaylist_IsRefused()
    {
        using var index = Index();
        var list = Path.Combine(_lists, "huge.m3u8");
        using (var stream = File.Create(list))
            stream.SetLength(M3uPlaylistFiles.MaxBytes + 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => M3uPlaylistFiles.ReadAsync(index, list));
    }
}
