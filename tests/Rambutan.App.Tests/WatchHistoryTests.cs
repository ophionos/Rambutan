using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.App.Tests;

/// <summary>Plays added to the account's YouTube Music history: only while on
/// and signed in, once each play counts.</summary>
public sealed class WatchHistoryTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();
    private readonly List<string> _sent = [];
    private bool _signedIn = true;

    private WatchHistory History(bool enabled = true, Func<Task>? fail = null) => new(
        async (videoId, _) =>
        {
            if (fail is not null)
                await fail();
            lock (_sent)
                _sent.Add(videoId);
        },
        () => _signedIn, enabled, _clock);

    private static TrackInfo Track(string id) => new() { VideoId = id, Title = "Song", Artists = [], Duration = TimeSpan.FromMinutes(4) };

    private void Play(WatchHistory history, string id, int seconds)
    {
        history.OnStateChanged(PlaybackState.Playing);
        history.OnNowPlayingChanged(Track(id));
        history.OnProgress(TimeSpan.Zero, null);
        for (var i = 1; i <= seconds; i++)
        {
            _clock.Now += TimeSpan.FromSeconds(1);
            history.OnProgress(TimeSpan.FromSeconds(i), null);
        }
    }

    private static async Task Drain(WatchHistory history)
    {
        history.Complete();
        await history.Worker.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_counted_play_is_sent_once()
    {
        var history = History();

        Play(history, "khnokW3Mw24", 40);
        Play(history, "YQHsXMglC9A", 10);
        await Drain(history);

        Assert.Equal(["khnokW3Mw24"], _sent);
    }

    [Fact]
    public async Task Off_or_signed_out_nothing_is_sent()
    {
        var off = History(enabled: false);
        Play(off, "khnokW3Mw24", 40);
        await Drain(off);

        _signedIn = false;
        var signedOut = History();
        Play(signedOut, "khnokW3Mw24", 40);
        await Drain(signedOut);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Turning_it_on_mid_track_doesnt_send_a_play_that_already_counted()
    {
        var history = History(enabled: false);
        Play(history, "khnokW3Mw24", 40);

        history.Enabled = true;
        for (var i = 41; i <= 120; i++)
        {
            _clock.Now += TimeSpan.FromSeconds(1);
            history.OnProgress(TimeSpan.FromSeconds(i), null);
        }
        await Drain(history);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task A_failed_report_is_dropped_and_the_next_still_goes()
    {
        var fails = 1;
        var history = History(fail: () => fails-- > 0 ? throw new IOException("yt-dlp failed") : Task.CompletedTask);

        Play(history, "khnokW3Mw24", 40);
        Play(history, "YQHsXMglC9A", 40);
        await Drain(history);

        Assert.Equal(["YQHsXMglC9A"], _sent);
        Assert.False(history.LastFailed);
    }

    [Fact]
    public async Task A_failure_shows_in_Settings_until_a_report_goes()
    {
        var fail = true;
        var history = History(fail: () => fail ? throw new IOException("yt-dlp failed") : Task.CompletedTask);
        var vm = new ListeningHistorySettingsViewModel(
            new AppSettings { AddPlaysToYouTubeHistory = true }, new PlayLog(new PlayLogStore(Path.GetTempPath()), false),
            () => { }, history, post: a => a());
        Assert.Null(vm.YouTubeHistoryStatus);

        Play(history, "khnokW3Mw24", 40);
        await Eventually(() => history.LastFailed);
        Assert.Contains("couldn't be added", vm.YouTubeHistoryStatus);

        fail = false;
        Play(history, "YQHsXMglC9A", 40);
        await Eventually(() => !history.LastFailed);
        Assert.Null(vm.YouTubeHistoryStatus);
    }

    [Fact]
    public void Signed_out_Settings_says_no_plays_are_added()
    {
        _signedIn = false;
        var vm = new ListeningHistorySettingsViewModel(
            new AppSettings(), new PlayLog(new PlayLogStore(Path.GetTempPath()), false), () => { }, History(),
            post: a => a());
        Assert.Null(vm.YouTubeHistoryStatus);

        vm.AddToYouTubeHistory = true;

        Assert.Contains("signed out", vm.YouTubeHistoryStatus);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
