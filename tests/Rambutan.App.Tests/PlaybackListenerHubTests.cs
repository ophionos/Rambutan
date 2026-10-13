using Rambutan.App.Services;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Integrations;

namespace Rambutan.App.Tests;

public sealed class PlaybackListenerHubTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Recorder : IPlaybackListener
    {
        public List<string> Events { get; } = [];
        public void OnNowPlayingChanged(TrackInfo? track) => Events.Add("track " + (track?.Title ?? "none"));
        public void OnStateChanged(PlaybackState state) => Events.Add("state " + state);
        public void OnProgress(TimeSpan position, TimeSpan? duration) => Events.Add($"at {position.TotalSeconds:0.#}");
    }

    private sealed class Thrower : IPlaybackListener
    {
        public void OnNowPlayingChanged(TrackInfo? track) => throw new InvalidOperationException();
        public void OnStateChanged(PlaybackState state) => throw new InvalidOperationException();
        public void OnProgress(TimeSpan position, TimeSpan? duration) => throw new InvalidOperationException();
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Progress_is_throttled_to_about_once_a_second()
    {
        var clock = new Clock();
        var hub = new PlaybackListenerHub(clock);
        var rec = new Recorder();
        hub.Add(rec);

        for (var i = 0; i <= 20; i++)
        {
            hub.OnProgress(S(i * 0.1), S(200));
            clock.Now += S(0.1);
        }

        Assert.Equal(["at 0", "at 1", "at 2"], rec.Events);
    }

    [Fact]
    public void A_seek_is_passed_on_at_once()
    {
        var clock = new Clock();
        var hub = new PlaybackListenerHub(clock);
        var rec = new Recorder();
        hub.Add(rec);

        hub.OnProgress(S(10), S(200));
        clock.Now += S(0.2);
        hub.OnProgress(S(90), S(200));   // forward seek
        clock.Now += S(0.2);
        hub.OnProgress(S(5), S(200));    // backward seek
        clock.Now += S(0.2);
        hub.OnProgress(S(5.2), S(200));  // ordinary tick, throttled

        Assert.Equal(["at 10", "at 90", "at 5"], rec.Events);
    }

    [Fact]
    public void A_new_track_sends_its_first_progress_immediately()
    {
        var clock = new Clock();
        var hub = new PlaybackListenerHub(clock);
        var rec = new Recorder();
        hub.Add(rec);

        hub.OnProgress(S(100), S(200));
        hub.OnNowPlayingChanged(new TrackInfo { VideoId = "v", Title = "Next", Artists = [] });
        hub.OnProgress(S(0), S(180));

        Assert.Equal(["at 100", "track Next", "at 0"], rec.Events);
    }

    [Fact]
    public void A_throwing_listener_does_not_stop_the_others()
    {
        var hub = new PlaybackListenerHub(new Clock());
        var rec = new Recorder();
        hub.Add(new Thrower());
        hub.Add(rec);

        hub.OnStateChanged(PlaybackState.Playing);
        hub.OnNowPlayingChanged(null);
        hub.OnProgress(S(1), null);

        Assert.Equal(["state Playing", "track none", "at 1"], rec.Events);
    }
}
