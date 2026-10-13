using Rambutan.Core.Models;
using Rambutan.Core.Queue;
using Rambutan.Integrations.Linux;

namespace Rambutan.Integrations.Tests;

public sealed class MprisPlayerStateTests
{
    internal static TrackInfo Track(string videoId = "abc-DEF_123", TimeSpan? duration = null) => new()
    {
        VideoId = videoId,
        Title = "Paper Lanterns",
        Artists = [new ArtistRef("The Quiet Harbour", "UC1"), new ArtistRef("Guest")],
        Album = "Tidelines",
        Duration = duration ?? TimeSpan.FromSeconds(212),
        ThumbnailUrl = "https://lh3.googleusercontent.com/art=w544-h544",
    };

    private static (MprisPlayerState State, StepClock Clock) Create()
    {
        var clock = new StepClock();
        return (new MprisPlayerState(clock), clock);
    }

    [Fact]
    public void Status_follows_the_track_and_playback()
    {
        var (state, _) = Create();
        Assert.Equal("Stopped", state.PlaybackStatus);
        Assert.Equal(MprisPlayerState.NoTrack, state.TrackId);

        // A track shown but not started (a restored queue) is stopped.
        state.SetTrack(Track());
        Assert.Equal("Stopped", state.PlaybackStatus);
        state.SetStatus(SessionPlaybackStatus.Playing);
        Assert.Equal("Playing", state.PlaybackStatus);
        state.SetStatus(SessionPlaybackStatus.Paused);
        Assert.Equal("Paused", state.PlaybackStatus);
        state.SetStatus(SessionPlaybackStatus.Stopped);
        Assert.Equal("Stopped", state.PlaybackStatus);

        state.SetStatus(SessionPlaybackStatus.Playing);
        state.SetTrack(null);
        Assert.Equal("Stopped", state.PlaybackStatus);
        Assert.False(state.IsPlaying);
    }

    [Fact]
    public void Stopped_is_at_the_start_and_reports_no_seek()
    {
        var (state, clock) = Create();
        state.SetTrack(Track());
        state.SetStatus(SessionPlaybackStatus.Playing);
        state.SetTimeline(TimeSpan.FromSeconds(80), null, out _, out _);

        state.SetStatus(SessionPlaybackStatus.Stopped);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.Zero, state.Position);
        state.SetTimeline(TimeSpan.FromSeconds(80), null, out var seeked, out _);
        Assert.False(seeked);
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(-0.2, 0.0)]
    [InlineData(1.7, 1.0)]
    public void Volume_stays_within_zero_and_one(double set, double expected)
    {
        var (state, _) = Create();
        Assert.Equal(1.0, state.Volume);

        state.SetVolume(set);

        Assert.Equal(expected, state.Volume);
        Assert.False(state.SetVolume(expected));
        Assert.False(state.SetVolume(double.NaN));
    }

    [Fact]
    public void Changes_report_only_what_changed()
    {
        var (state, _) = Create();
        var track = Track();

        Assert.True(state.SetTrack(track));
        Assert.False(state.SetTrack(track));
        Assert.True(state.SetStatus(SessionPlaybackStatus.Playing));
        Assert.False(state.SetStatus(SessionPlaybackStatus.Playing));
        Assert.True(state.SetShuffle(true));
        Assert.False(state.SetShuffle(true));
        Assert.True(state.SetRepeat(RepeatMode.All));
        Assert.False(state.SetRepeat(RepeatMode.All));
    }

    [Theory]
    [InlineData(RepeatMode.Off, "None")]
    [InlineData(RepeatMode.One, "Track")]
    [InlineData(RepeatMode.All, "Playlist")]
    public void Loop_status_names_the_repeat_mode_both_ways(RepeatMode repeat, string loopStatus)
    {
        var (state, _) = Create();
        state.SetRepeat(repeat);

        Assert.Equal(loopStatus, state.LoopStatus);
        Assert.Equal(repeat, MprisPlayerState.FromLoopStatus(loopStatus));
    }

    [Theory]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("All")]
    public void Loop_status_outside_the_spec_names_no_mode(string loopStatus) =>
        Assert.Null(MprisPlayerState.FromLoopStatus(loopStatus));

    [Fact]
    public void Shuffle_and_loop_start_off()
    {
        var (state, _) = Create();

        Assert.False(state.Shuffle);
        Assert.Equal("None", state.LoopStatus);
    }

    [Theory]
    [InlineData("abc-DEF_123")]
    [InlineData("-_-_-_-_-_-")]
    [InlineData("ü")]
    public void Track_ids_are_valid_object_paths_and_tell_tracks_apart(string videoId)
    {
        var id = MprisPlayerState.TrackIdFor(videoId);

        Assert.StartsWith(MprisPlayerState.TrackIdPrefix, id);
        // An object path element is [A-Za-z0-9_] only.
        Assert.Matches("^(/[A-Za-z0-9_]+)+$", id);
        Assert.NotEqual(id, MprisPlayerState.TrackIdFor(videoId + "x"));
    }

    [Fact]
    public void Metadata_uses_the_types_the_spec_gives_each_key()
    {
        var (state, _) = Create();
        state.SetTrack(Track());

        var metadata = state.Metadata().ToDictionary(e => e.Key, e => e.Value);

        Assert.Equal(MprisValue.OfPath(MprisPlayerState.TrackIdFor("abc-DEF_123")), metadata["mpris:trackid"]);
        Assert.Equal(MprisValue.OfText("Paper Lanterns"), metadata["xesam:title"]);
        var artists = metadata["xesam:artist"];
        Assert.Equal('a', artists.Kind);
        Assert.Equal(["The Quiet Harbour", "Guest"], artists.List!);
        Assert.Equal(MprisValue.OfText("Tidelines"), metadata["xesam:album"]);
        Assert.Equal(new MprisValue('x', Number: 212_000_000), metadata["mpris:length"]);
        Assert.Equal(MprisValue.OfText("https://lh3.googleusercontent.com/art=w544-h544"), metadata["mpris:artUrl"]);
        Assert.Equal(MprisValue.OfText("https://music.youtube.com/watch?v=abc-DEF_123"), metadata["xesam:url"]);
    }

    [Fact]
    public void Metadata_leaves_out_what_is_unknown()
    {
        var (state, _) = Create();
        state.SetTrack(Track() with { Album = " ", Duration = null, ThumbnailUrl = null });

        Assert.Equal(["mpris:trackid", "xesam:title", "xesam:artist", "xesam:url"], state.Metadata().Select(e => e.Key));
        Assert.False(state.CanSeek);

        state.SetTrack(null);
        Assert.Equal([new MprisMetadataEntry("mpris:trackid", MprisValue.OfPath(MprisPlayerState.NoTrack))], state.Metadata());
    }

    [Fact]
    public void The_timeline_supplies_a_length_the_catalog_lacked()
    {
        var (state, _) = Create();
        state.SetTrack(Track() with { Duration = null });

        state.SetTimeline(TimeSpan.Zero, TimeSpan.FromSeconds(200), out _, out var changed);

        Assert.True(changed);
        Assert.True(state.CanSeek);
        Assert.Contains(state.Metadata(), e => e.Key == "mpris:length");
        state.SetTimeline(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(200), out _, out changed);
        Assert.False(changed);
    }

    [Fact]
    public void Position_moves_on_while_playing_and_holds_while_paused()
    {
        var (state, clock) = Create();
        state.SetTrack(Track());
        state.SetStatus(SessionPlaybackStatus.Paused);
        state.SetTimeline(TimeSpan.FromSeconds(10), null, out _, out _);

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(10), state.Position);

        state.SetStatus(SessionPlaybackStatus.Playing);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(12), state.Position);

        state.SetStatus(SessionPlaybackStatus.Paused);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(12), state.Position);
    }

    [Fact]
    public void Position_stops_at_the_end()
    {
        var (state, clock) = Create();
        state.SetTrack(Track(duration: TimeSpan.FromSeconds(30)));
        state.SetStatus(SessionPlaybackStatus.Playing);
        state.SetTimeline(TimeSpan.FromSeconds(29), null, out _, out _);

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(30), state.Position);
    }

    [Fact]
    public void A_jump_is_a_seek_and_steady_progress_is_not()
    {
        var (state, clock) = Create();
        state.SetTrack(Track());
        state.SetStatus(SessionPlaybackStatus.Playing);

        for (var i = 1; i <= 8; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            state.SetTimeline(TimeSpan.FromMilliseconds(250 * i + 40), null, out var steady, out _);
            Assert.False(steady);
        }

        state.SetTimeline(TimeSpan.FromSeconds(90), null, out var forward, out _);
        Assert.True(forward);
        state.SetTimeline(TimeSpan.FromSeconds(5), null, out var back, out _);
        Assert.True(back);
    }

    [Fact]
    public void A_new_track_starting_at_zero_is_not_a_seek()
    {
        var (state, clock) = Create();
        state.SetTrack(Track("one"));
        state.SetStatus(SessionPlaybackStatus.Playing);
        state.SetTimeline(TimeSpan.FromSeconds(200), null, out _, out _);

        state.SetTrack(Track("two"));
        clock.Advance(TimeSpan.FromMilliseconds(300));
        state.SetTimeline(TimeSpan.FromMilliseconds(250), null, out var seeked, out _);

        Assert.False(seeked);
    }

    [Fact]
    public void Microseconds_convert_both_ways()
    {
        Assert.Equal(212_000_000, MprisPlayerState.ToMicroseconds(TimeSpan.FromSeconds(212)));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), MprisPlayerState.FromMicroseconds(1_500_000));
    }
}

/// <summary>A monotonic clock the test moves by hand.</summary>
internal sealed class StepClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}
