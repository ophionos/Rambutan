using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.Core.Tests;

/// <summary>When a play goes to the account's YouTube Music history.</summary>
public class HistoryReportGateTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly ManualClock _clock = new();
    private readonly HistoryReportGate _gate;
    private readonly List<string> _reported = [];
    private TimeSpan _position;

    public HistoryReportGateTests() => _gate = new HistoryReportGate(_clock);

    private static TrackInfo Track(string id, double? seconds = 240) => new()
    {
        VideoId = id,
        Title = "Song",
        Artists = [],
        Duration = seconds is { } s ? TimeSpan.FromSeconds(s) : null,
    };

    private void Start(TrackInfo track)
    {
        _gate.OnStateChanged(PlaybackState.Playing);
        _gate.OnNowPlaying(track);
        _position = TimeSpan.Zero;
        Tick();
    }

    private void Play(int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            _position += TimeSpan.FromSeconds(1);
            Tick();
        }
    }

    private void Tick()
    {
        if (_gate.OnProgress(_position, null) is { } id)
            _reported.Add(id);
    }

    [Fact]
    public void A_play_is_reported_once_after_30_seconds_heard()
    {
        Start(Track("khnokW3Mw24"));

        Play(29);
        Assert.Empty(_reported);
        Play(60);

        Assert.Equal(["khnokW3Mw24"], _reported);
    }

    [Fact]
    public void A_short_track_counts_at_half()
    {
        Start(Track("khnokW3Mw24", 20));

        Play(9);
        Assert.Empty(_reported);
        Play(1);

        Assert.Equal(["khnokW3Mw24"], _reported);
    }

    [Fact]
    public void A_skip_before_it_counts_reports_nothing_and_a_replay_counts_again()
    {
        Start(Track("khnokW3Mw24"));
        Play(10);
        Start(Track("YQHsXMglC9A"));
        Play(30);
        Start(Track("YQHsXMglC9A"));
        Play(30);

        Assert.Equal(["YQHsXMglC9A", "YQHsXMglC9A"], _reported);
    }

    [Fact]
    public void A_seek_or_a_pause_adds_nothing()
    {
        Start(Track("khnokW3Mw24"));
        _position = TimeSpan.FromSeconds(200);
        Tick();
        _gate.OnStateChanged(PlaybackState.Paused);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Tick();

        Assert.Empty(_reported);
    }

    [Theory]
    [InlineData("local:3")]
    [InlineData("")]
    public void A_track_that_isnt_on_YouTube_is_never_reported(string id)
    {
        Start(Track(id));
        Play(60);

        Assert.Empty(_reported);
    }
}
