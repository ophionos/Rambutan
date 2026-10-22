using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.LocalLibrary;

namespace Rambutan.App.Tests;

/// <summary>Files opened (Ctrl+O) or dropped on the window: where they go in the
/// queue, and what is said.</summary>
public sealed class LocalFileOpenerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-open-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileIndex _index;
    private readonly ToastHostViewModel _toasts = new(new ManualClock().Schedule);
    private readonly List<IReadOnlyList<TrackInfo>> _played = [];
    private readonly List<IReadOnlyList<TrackInfo>> _queued = [];
    private readonly LocalFileOpener _opener;

    public LocalFileOpenerTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "music"));
        _index = new LocalFileIndex(Path.Combine(_dir, "data"), new TitleReader(), _ => Task.CompletedTask, () => []);
        _opener = new LocalFileOpener(_index,
            tracks => { _played.Add(tracks); return Task.CompletedTask; },
            _queued.Add, _toasts);
    }

    public void Dispose()
    {
        _index.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>The reader takes a file's text as its title; "bad" isn't audio.</summary>
    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, "music", name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Opened_files_play_after_the_current_track_in_their_order()
    {
        string[] paths = [Write("b.mp3", "B"), Write("a.mp3", "A")];

        await _opener.OpenAsync(paths, playNow: true);

        var played = Assert.Single(_played);
        Assert.Equal(["B", "A"], played.Select(t => t.Title));
        Assert.All(played, t => Assert.True(LocalIds.IsLocal(t.VideoId)));
        Assert.Empty(_queued);
        Assert.Empty(_toasts.Toasts);
    }

    [Fact]
    public async Task Files_dropped_on_the_queue_go_to_its_end_and_it_says_how_many()
    {
        await _opener.OpenAsync([Path.Combine(_dir, "music")], playNow: false);
        Assert.Equal("Nothing to play: no music file was found there, or it couldn't be read.",
            Assert.Single(_toasts.Toasts).Message);

        Write("a.mp3", "A");
        Write("b.mp3", "B");
        await _opener.OpenAsync([Path.Combine(_dir, "music")], playNow: false);

        Assert.Equal(["A", "B"], Assert.Single(_queued).Select(t => t.Title));
        Assert.Empty(_played);
        Assert.Equal("Added 2 songs to the queue", _toasts.Toasts[^1].Message);
    }

    [Fact]
    public async Task One_file_queued_is_named()
    {
        await _opener.OpenAsync([Write("a.mp3", "Alone")], playNow: false);

        Assert.Equal("Added “Alone” to the queue", Assert.Single(_toasts.Toasts).Message);
    }

    [Fact]
    public async Task Nothing_playable_says_so_and_changes_nothing()
    {
        await _opener.OpenAsync([Write("notes.mp3", "bad"), Path.Combine(_dir, "gone.mp3")], playNow: true);

        Assert.Empty(_played);
        Assert.Equal(ToastKind.Error, Assert.Single(_toasts.Toasts).Kind);
    }

    private sealed class TitleReader : IMediaFileReader
    {
        public LocalFileTags? Read(string path)
        {
            var text = File.ReadAllText(path);
            return text == "bad" ? null : new LocalFileTags("mp3", Title: text);
        }
    }
}
