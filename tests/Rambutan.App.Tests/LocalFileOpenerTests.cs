using System.Collections.Concurrent;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.LocalLibrary;

namespace Rambutan.App.Tests;

/// <summary>Files opened (Ctrl+O) or dropped on the window: where they go in the
/// queue, and what is said, during the read too.</summary>
public sealed class LocalFileOpenerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-open-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileIndex _index;
    private readonly ManualClock _clock = new();
    private readonly TitleReader _reader = new();
    private readonly ToastHostViewModel _toasts;
    private readonly List<IReadOnlyList<TrackInfo>> _played = [];
    private readonly List<IReadOnlyList<TrackInfo>> _queued = [];
    private readonly LocalFileOpener _opener;

    public LocalFileOpenerTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "music"));
        _toasts = new ToastHostViewModel(_clock.Schedule);
        _index = new LocalFileIndex(Path.Combine(_dir, "data"), _reader, Ready, () => []);
        _opener = new LocalFileOpener(_index,
            tracks => { _played.Add(tracks); return Task.CompletedTask; },
            _queued.Add, _toasts, _clock.Schedule);
    }

    /// <summary>When set, the index waits for it, as for ffmpeg on a first start.</summary>
    private TaskCompletionSource? _ffmpeg;

    private Task Ready(CancellationToken ct) => _ffmpeg?.Task.WaitAsync(ct) ?? Task.CompletedTask;

    private static async Task Eventually(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
            await Task.Delay(10);
        Assert.True(done());
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    [Fact]
    public async Task A_read_that_ends_quickly_never_shows()
    {
        var changes = 0;
        _opener.ReadingChanged += () => changes++;

        await _opener.OpenAsync([Write("a.mp3", "A")], playNow: true);
        _clock.Advance(LocalFileOpener.ShowAfter);

        Assert.Null(_opener.Reading);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task A_long_read_shows_while_it_waits_then_how_many_files_and_goes_when_it_ends()
    {
        Write("a.mp3", "A");
        Write("b.mp3", "B");
        _ffmpeg = new TaskCompletionSource();
        var held = _reader.Hold("a.mp3");

        var open = _opener.OpenAsync([Path.Combine(_dir, "music")], playNow: true);
        Assert.Null(_opener.Reading);
        _clock.Advance(LocalFileOpener.ShowAfter);
        Assert.Equal(0, _opener.Reading);

        _ffmpeg.SetResult();
        await Eventually(() => _opener.Reading == 2);
        held.Set();
        await open;

        Assert.Null(_opener.Reading);
        Assert.Single(_played);
    }

    [Fact]
    public async Task Overlapping_reads_add_up_and_show_until_the_last_ends()
    {
        var a = Write(Path.Combine("one", "a.mp3"), "A");
        Write(Path.Combine("two", "b.mp3"), "B");
        Write(Path.Combine("two", "c.mp3"), "C");
        _ffmpeg = new TaskCompletionSource();
        var heldA = _reader.Hold("a.mp3");
        var heldB = _reader.Hold("b.mp3");

        var first = _opener.OpenAsync([a], playNow: true);
        _clock.Advance(LocalFileOpener.ShowAfter);
        var second = _opener.OpenAsync([Path.Combine(_dir, "music", "two")], playNow: false);
        _ffmpeg.SetResult();
        await Eventually(() => _opener.Reading == 3);

        heldA.Set();
        await first;
        Assert.Equal(2, _opener.Reading);

        heldB.Set();
        await second;
        Assert.Null(_opener.Reading);
    }

    [Fact]
    public async Task A_read_that_fails_goes_and_says_why()
    {
        _ffmpeg = new TaskCompletionSource();
        var open = _opener.OpenAsync([Write("a.mp3", "A")], playNow: true);
        _clock.Advance(LocalFileOpener.ShowAfter);
        Assert.Equal(0, _opener.Reading);

        _ffmpeg.SetException(new HttpRequestException("ffmpeg didn't download"));
        await open;

        Assert.Null(_opener.Reading);
        var toast = Assert.Single(_toasts.Toasts);
        Assert.Equal(ToastKind.Error, toast.Kind);
        Assert.Equal("Audio setup failed — playback unavailable.", toast.Message);
        Assert.Empty(_played);
    }

    private sealed class TitleReader : IMediaFileReader
    {
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> _holds = new();

        /// <summary>Reads of the file with this name wait until the result is set.</summary>
        public ManualResetEventSlim Hold(string name) => _holds[name] = new ManualResetEventSlim();

        public LocalFileTags? Read(string path)
        {
            if (_holds.TryGetValue(Path.GetFileName(path), out var hold))
                hold.Wait(TimeSpan.FromSeconds(10));
            var text = File.ReadAllText(path);
            return text == "bad" ? null : new LocalFileTags("mp3", Title: text);
        }
    }
}
