using Rambutan.App.Services;
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
    private readonly List<(string VideoId, string Cookies)> _sent = [];
    private string? _cookies = "SAPISID=a";

    private WatchHistory History(bool enabled = true, Func<Task>? fail = null) => new(
        async (videoId, cookies, _) =>
        {
            if (fail is not null)
                await fail();
            lock (_sent)
                _sent.Add((videoId, cookies));
        },
        () => _cookies, enabled, _clock);

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
    public async Task A_counted_play_is_sent_with_the_accounts_cookies()
    {
        var history = History();

        Play(history, "khnokW3Mw24", 40);
        Play(history, "YQHsXMglC9A", 10);
        await Drain(history);

        Assert.Equal([("khnokW3Mw24", "SAPISID=a")], _sent);
    }

    [Fact]
    public async Task Off_or_signed_out_nothing_is_sent()
    {
        var off = History(enabled: false);
        Play(off, "khnokW3Mw24", 40);
        await Drain(off);

        _cookies = null;
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

        Assert.Equal(["YQHsXMglC9A"], _sent.Select(s => s.VideoId));
    }
}
