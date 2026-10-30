using System.Text;
using Rambutan.App.Services;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Stats;

namespace Rambutan.App.Tests;

/// <summary>
/// The play log: <c>plays.jsonl</c> as written and read back, a newer or broken
/// file left alone, and the recorder's plays as heard, kept off, and cleared.
/// </summary>
public sealed class PlayLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-plays-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string LogPath => Path.Combine(_dir, "plays.jsonl");

    private static TrackInfo Track(string id, string title = "Song") => new()
    {
        VideoId = id,
        Title = title,
        Artists = [new("Band", "UCband")],
        Album = "Record",
        Duration = TimeSpan.FromMinutes(4),
    };

    private PlayRecord Record(string id, double minutes) =>
        PlayRecord.From(id, Track("aaaaaaaaaaa"), _clock.Now, TimeSpan.FromMinutes(minutes));

    [Fact]
    public void A_play_written_again_replaces_the_first_line()
    {
        var store = new PlayLogStore(_dir);
        store.Write(Record("1", 1));
        store.Write(Record("2", 2));
        store.Write(Record("1", 3));

        var read = new PlayLogStore(_dir).Read();

        Assert.Equal(PlayLogState.Readable, read.State);
        Assert.Equal([("1", 3.0), ("2", 2.0)], read.Plays.Select(p => (p.Id, p.Listened.TotalMinutes)).Order());
        Assert.StartsWith("{\"version\":1}\n", File.ReadAllText(LogPath));
        Assert.DoesNotContain("songKey", File.ReadAllText(LogPath));
    }

    [Fact]
    public void A_line_cut_short_is_skipped_and_the_next_play_starts_a_line_of_its_own()
    {
        var store = new PlayLogStore(_dir);
        store.Write(Record("1", 1));
        File.AppendAllText(LogPath, "{\"id\":\"2\",\"startedAt\":\"2026-");

        store.Write(Record("3", 3));

        Assert.Equal(["1", "3"], new PlayLogStore(_dir).Read().Plays.Select(p => p.Id));
    }

    [Fact]
    public void A_newer_file_is_read_as_newer_and_left_as_it_is()
    {
        Directory.CreateDirectory(_dir);
        const string newer = "{\"version\":2}\n{\"what\":\"a newer build keeps\"}\n";
        File.WriteAllText(LogPath, newer);
        var store = new PlayLogStore(_dir);

        Assert.Equal(PlayLogState.Newer, store.Read().State);
        store.Write(Record("1", 1));
        new PlayLogStore(_dir).Write(Record("2", 1));

        Assert.Equal(newer, File.ReadAllText(LogPath));
    }

    [Fact]
    public void A_file_without_a_readable_header_is_left_until_it_is_cleared()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LogPath, "not json\n");
        var store = new PlayLogStore(_dir);

        Assert.Equal(PlayLogState.Unreadable, store.Read().State);
        store.Write(Record("1", 1));
        Assert.Equal("not json\n", File.ReadAllText(LogPath));

        store.Delete();
        store.Write(Record("2", 1));
        Assert.Equal(["2"], new PlayLogStore(_dir).Read().Plays.Select(p => p.Id));
    }

    [Fact]
    public void Reading_a_file_full_of_replaced_lines_writes_it_again_without_them()
    {
        var store = new PlayLogStore(_dir);
        for (var i = 1; i <= 300; i++)
            store.Write(Record("1", i / 100.0));

        var read = new PlayLogStore(_dir).Read();

        Assert.Single(read.Plays);
        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
        Assert.Equal(3, new PlayLogStore(_dir).Read().Plays[0].Listened.TotalMinutes);
    }

    private PlayLog Recorder(bool enabled = true) => new(new PlayLogStore(_dir), enabled, _clock);

    /// <summary>Plays <paramref name="seconds"/>, one progress event a second.</summary>
    private void Play(PlayLog log, int seconds, ref TimeSpan position)
    {
        for (var i = 0; i < seconds; i++)
        {
            _clock.Now += TimeSpan.FromSeconds(1);
            position += TimeSpan.FromSeconds(1);
            log.OnProgress(position, TimeSpan.FromMinutes(4));
        }
    }

    private static void Start(PlayLog log, TrackInfo track, ref TimeSpan position)
    {
        log.OnNowPlayingChanged(track);
        log.OnStateChanged(PlaybackState.Playing);
        position = TimeSpan.Zero;
        log.OnProgress(position, track.Duration);
    }

    [Fact]
    public async Task Each_play_is_recorded_with_what_was_heard()
    {
        using var log = Recorder();
        var position = TimeSpan.Zero;
        var startedAt = _clock.Now;

        Start(log, Track("aaaaaaaaaaa", "First"), ref position);
        Play(log, 40, ref position);
        Start(log, Track("bbbbbbbbbbb", "Second"), ref position);
        Play(log, 10, ref position);

        var read = await log.ReadAsync();

        Assert.Equal([("First", 40.0), ("Second", 10.0)],
            read.Plays.Select(p => (p.Title, p.Listened.TotalSeconds)));
        Assert.Equal(startedAt, read.Plays[0].StartedAt);
    }

    [Fact]
    public void A_pause_writes_the_play_so_far_and_the_play_goes_on_under_the_same_id()
    {
        var log = Recorder();
        var position = TimeSpan.Zero;
        Start(log, Track("aaaaaaaaaaa"), ref position);
        Play(log, 20, ref position);

        log.OnStateChanged(PlaybackState.Paused);
        _clock.Now += TimeSpan.FromMinutes(10);
        log.OnStateChanged(PlaybackState.Playing);
        log.OnProgress(position, null);
        Play(log, 15, ref position);
        log.Dispose();

        var plays = new PlayLogStore(_dir).Read().Plays;
        Assert.Equal(35, Assert.Single(plays).Listened.TotalSeconds);
        Assert.Equal(2, File.ReadAllLines(LogPath).Length - 1);
    }

    [Fact]
    public async Task Off_nothing_is_recorded_and_on_again_the_track_counts_from_then()
    {
        using var log = Recorder(enabled: false);
        var position = TimeSpan.Zero;
        Start(log, Track("aaaaaaaaaaa"), ref position);
        Play(log, 30, ref position);
        Assert.Empty((await log.ReadAsync()).Plays);

        log.Enabled = true;
        Play(log, 12, ref position);

        Assert.Equal(12, Assert.Single((await log.ReadAsync()).Plays).Listened.TotalSeconds);
    }

    [Fact]
    public async Task Turning_it_off_forgets_the_play_running()
    {
        using var log = Recorder();
        var position = TimeSpan.Zero;
        Start(log, Track("aaaaaaaaaaa"), ref position);
        Play(log, 30, ref position);

        log.Enabled = false;
        log.OnStateChanged(PlaybackState.Paused);

        Assert.Empty((await log.ReadAsync()).Plays);
    }

    [Fact]
    public async Task Clearing_deletes_the_log_and_the_play_running_starts_from_nothing()
    {
        using var log = Recorder();
        var position = TimeSpan.Zero;
        Start(log, Track("aaaaaaaaaaa", "Before"), ref position);
        Play(log, 40, ref position);
        Start(log, Track("bbbbbbbbbbb", "Running"), ref position);
        Play(log, 30, ref position);

        await log.ClearAsync();
        Play(log, 5, ref position);

        var play = Assert.Single((await log.ReadAsync()).Plays);
        Assert.Equal(("Running", 5.0), (play.Title, play.Listened.TotalSeconds));
    }

    [Fact]
    public async Task A_long_play_is_written_every_few_minutes_as_it_goes()
    {
        using var log = Recorder();
        var position = TimeSpan.Zero;
        Start(log, Track("aaaaaaaaaaa") with { Duration = TimeSpan.FromHours(1) }, ref position);

        Play(log, (int)PlayLog.WriteEvery.TotalSeconds - 1, ref position);
        Play(log, 1, ref position);
        Assert.Equal(2, await WaitForLinesAsync(2));

        Play(log, (int)PlayLog.WriteEvery.TotalSeconds, ref position);
        Assert.Equal(3, await WaitForLinesAsync(3));
    }

    /// <summary>The worker writes on its own time; the file may be open for its
    /// write while this looks.</summary>
    private async Task<int> WaitForLinesAsync(int expected)
    {
        var lines = 0;
        for (var i = 0; i < 250 && lines < expected; i++)
        {
            await Task.Delay(20);
            try
            {
                lines = File.Exists(LogPath) ? File.ReadAllLines(LogPath, Encoding.UTF8).Length : 0;
            }
            catch (IOException)
            {
            }
        }
        return lines;
    }
}
