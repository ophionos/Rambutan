using System.Collections.ObjectModel;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Local;
using Rambutan.LocalLibrary;

namespace Rambutan.App.Tests;

/// <summary>Library → On this computer: what it lists, its states, and how the
/// rows follow a scan without being rebuilt.</summary>
public sealed class LocalLibraryViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-locallib-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _folders = [];
    private readonly LocalLibraryScans _scans;
    private readonly LocalLibraryViewModel _vm;

    public LocalLibraryViewModelTests()
    {
        Directory.CreateDirectory(Music);
        _scans = new LocalLibraryScans(new LocalFileIndex(
            Path.Combine(_dir, "data"), new TagReader(), _ => Task.CompletedTask, () => _folders));
        // Shown by hand: a scan thread's refresh would race the test's.
        _vm = new LocalLibraryViewModel(_scans, coordinator: null, () => _folders, post: _ => { });
    }

    public void Dispose()
    {
        _scans.Index.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Music => Path.Combine(_dir, "music");

    /// <summary>The reader takes "artist|album|track|title" from the file; "bad" isn't audio.</summary>
    private void Write(string name, string content) => File.WriteAllText(Path.Combine(Music, name), content);

    private async Task ScanAndShowAsync()
    {
        _folders.Clear();
        _folders.Add(Music);
        await _scans.Index.ScanAsync();
        await _vm.ShowAsync();
    }

    [Fact]
    public async Task WithNoFolders_ItSaysSo_AndListsNothing()
    {
        await _vm.ShowAsync();

        Assert.False(_vm.HasFolders);
        Assert.False(_vm.IsEmpty);
        Assert.Empty(_vm.Tracks);
    }

    [Fact]
    public async Task ItListsTheReadableFiles_InLibraryOrder_AndCountsTheRest()
    {
        Write("1.mp3", "B|Album|2|Second");
        Write("2.mp3", "A|Album|1|Only");
        Write("3.mp3", "B|Album|1|First");
        Write("4.mp3", "bad");

        await ScanAndShowAsync();

        Assert.Equal(["Only", "First", "Second"], _vm.Tracks.Select(r => r.Title));
        Assert.Equal("B · Album", _vm.Tracks[1].Subtitle);
        Assert.Equal("First, B · Album", _vm.Tracks[1].AccessibleName);
        Assert.True(_vm.HasFolders);
        Assert.False(_vm.IsEmpty);
        Assert.False(_vm.IsScanning);
        Assert.Equal("3 songs", _vm.Status);
        Assert.Equal("1 file couldn't be read as music.", _vm.Problems);
    }

    [Fact]
    public async Task FoldersWithNoMusic_AreEmpty()
    {
        Write("a.mp3", "bad");

        await ScanAndShowAsync();

        Assert.True(_vm.IsEmpty);
        Assert.Empty(_vm.Tracks);
    }

    [Fact]
    public async Task AMissingFile_LeavesTheList_AndIsCounted()
    {
        Write("a.mp3", "A|X|1|Stays");
        Write("b.mp3", "A|X|2|Goes");
        await ScanAndShowAsync();

        File.Delete(Path.Combine(Music, "b.mp3"));
        await ScanAndShowAsync();

        Assert.Equal(["Stays"], _vm.Tracks.Select(r => r.Title));
        Assert.Equal("1 file is missing.", _vm.Problems);
    }

    [Fact]
    public async Task WhileHidden_AScanDoesntTouchTheList()
    {
        Write("a.mp3", "A|X|1|One");
        await ScanAndShowAsync();
        _vm.Hide();
        Write("b.mp3", "A|X|2|Two");

        await _scans.Index.ScanAsync();

        Assert.Single(_vm.Tracks);
        await _vm.ShowAsync();
        Assert.Equal(2, _vm.Tracks.Count);
    }

    [Fact]
    public async Task ARow_PlaysTheWholeList_StartingAtIt()
    {
        Write("1.mp3", "A|X|1|One");
        Write("2.mp3", "A|X|2|Two");
        Write("3.mp3", "A|X|3|Three");
        await ScanAndShowAsync();
        var queue = new Core.Queue.QueueModel();
        var vm = new LocalLibraryViewModel(_scans, FakePlayback.Coordinator(queue), () => _folders, post: _ => { });
        await vm.ShowAsync();

        await vm.PlayCommand.ExecuteAsync(vm.Tracks[1]);

        Assert.Equal(vm.Tracks.Select(r => r.Id), queue.Items.Select(t => t.VideoId));
        Assert.Equal(1, queue.CurrentIndex);
    }

    private static LocalTrackRow Row(string id, string title) =>
        new(new LocalFile(id, "/m/" + id, 1, DateTimeOffset.UnixEpoch, new LocalFileTags("mp3", Title: title)),
            new Core.Models.TrackInfo { VideoId = id, Title = title, Artists = [] });

    [Fact]
    public void Apply_EditsTheRowsInPlace_KeepingTheOnesThatDidntChange()
    {
        var a = Row("local:1", "a");
        var b = Row("local:2", "b");
        var c = Row("local:3", "c");
        var rows = new ObservableCollection<LocalTrackRow> { a, b, c };
        var changes = 0;
        rows.CollectionChanged += (_, e) =>
        {
            changes++;
            Assert.NotEqual(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, e.Action);
        };

        var renamed = Row("local:3", "c, retagged");
        var added = Row("local:4", "d");
        LocalLibraryViewModel.Apply(rows, [renamed, a, added]);

        Assert.Equal(["local:3", "local:1", "local:4"], rows.Select(r => r.Id));
        Assert.Same(a, rows[1]);
        Assert.Same(renamed, rows[0]);
        Assert.Equal(4, changes); // remove b, move c, replace c, insert d
    }

    [Fact]
    public void Apply_WithNothingChanged_ChangesNothing()
    {
        var rows = new ObservableCollection<LocalTrackRow> { Row("local:1", "a"), Row("local:2", "b") };
        var changes = 0;
        rows.CollectionChanged += (_, _) => changes++;

        LocalLibraryViewModel.Apply(rows, [Row("local:1", "a"), Row("local:2", "b")]);

        Assert.Equal(0, changes);
    }

    private sealed class TagReader : IMediaFileReader
    {
        public LocalFileTags? Read(string path)
        {
            var text = File.ReadAllText(path);
            if (text == "bad")
                return null;
            var parts = text.Split('|');
            return new LocalFileTags("mp3", Title: parts[3], Artist: parts[0], Album: parts[1],
                TrackNumber: int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
