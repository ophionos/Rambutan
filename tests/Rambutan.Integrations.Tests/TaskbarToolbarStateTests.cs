using Rambutan.Core.Models;
using Rambutan.Integrations.Taskbar;

namespace Rambutan.Integrations.Tests;

public sealed class TaskbarToolbarStateTests
{
    private static readonly TaskbarInput Playing = new(
        IsOn: true, HasQueue: true, IsPlaying: true, Track: MprisPlayerStateTests.Track(),
        Rating: Rating.None, IsSignedIn: true, Shuffle: false);

    private static readonly TrackInfo Loaded = MprisPlayerStateTests.Track(duration: TimeSpan.FromSeconds(212));

    private static TaskbarButtonView Button(TaskbarInput input, TaskbarButton button) =>
        TaskbarToolbarState.Buttons(input).Single(b => b.Button == button);

    [Fact]
    public void The_five_buttons_come_in_order_with_their_ids()
    {
        var buttons = TaskbarToolbarState.Buttons(Playing);

        Assert.Equal(
            [TaskbarButton.Previous, TaskbarButton.PlayPause, TaskbarButton.Next, TaskbarButton.Like, TaskbarButton.Shuffle],
            buttons.Select(b => b.Button));
        Assert.All(buttons, b => Assert.True(b.IsEnabled));
        Assert.All(buttons, b => Assert.False(b.IsHidden));
    }

    [Fact]
    public void Play_and_pause_swap_with_playback()
    {
        Assert.Equal(TaskbarGlyph.Pause, Button(Playing, TaskbarButton.PlayPause).Glyph);
        Assert.Equal("taskbar.pause", Button(Playing, TaskbarButton.PlayPause).TipKey);

        var paused = Playing with { IsPlaying = false };
        Assert.Equal(TaskbarGlyph.Play, Button(paused, TaskbarButton.PlayPause).Glyph);
        Assert.Equal("taskbar.play", Button(paused, TaskbarButton.PlayPause).TipKey);
    }

    [Fact]
    public void Without_a_queue_transport_is_disabled_as_in_the_player_bar()
    {
        var empty = Playing with { HasQueue = false, IsPlaying = false, Track = null };

        foreach (var button in new[] { TaskbarButton.Previous, TaskbarButton.PlayPause, TaskbarButton.Next })
            Assert.False(Button(empty, button).IsEnabled);
        Assert.True(Button(empty, TaskbarButton.Shuffle).IsEnabled);
    }

    [Fact]
    public void Like_shows_the_rating_and_names_what_a_press_does()
    {
        Assert.Equal(TaskbarGlyph.Like, Button(Playing, TaskbarButton.Like).Glyph);
        Assert.Equal("rating.like", Button(Playing, TaskbarButton.Like).TipKey);

        var liked = Playing with { Rating = Rating.Like };
        Assert.Equal(TaskbarGlyph.Liked, Button(liked, TaskbarButton.Like).Glyph);
        Assert.Equal("rating.removeLike", Button(liked, TaskbarButton.Like).TipKey);

        // Unknown or disliked reads as not liked: Like is what a press gives.
        Assert.Equal(TaskbarGlyph.Like, Button(Playing with { Rating = null }, TaskbarButton.Like).Glyph);
        Assert.Equal(TaskbarGlyph.Like, Button(Playing with { Rating = Rating.Dislike }, TaskbarButton.Like).Glyph);
    }

    [Fact]
    public void Like_is_disabled_signed_out_or_without_a_track()
    {
        var signedOut = Button(Playing with { IsSignedIn = false }, TaskbarButton.Like);
        Assert.False(signedOut.IsEnabled);
        Assert.Equal("taskbar.likeSignedOut", signedOut.TipKey);

        Assert.False(Button(Playing with { Track = null }, TaskbarButton.Like).IsEnabled);
    }

    [Fact]
    public void Shuffle_carries_its_mode_in_the_glyph()
    {
        Assert.Equal(TaskbarGlyph.ShuffleOff, Button(Playing, TaskbarButton.Shuffle).Glyph);
        Assert.Equal("player.shuffleOff", Button(Playing, TaskbarButton.Shuffle).TipKey);

        var on = Button(Playing with { Shuffle = true }, TaskbarButton.Shuffle);
        Assert.Equal(TaskbarGlyph.ShuffleOn, on.Glyph);
        Assert.Equal("player.shuffleOn", on.TipKey);
    }

    [Fact]
    public void Turned_off_every_button_is_hidden()
    {
        // Windows can't take buttons away, so off is all of them hidden.
        Assert.All(TaskbarToolbarState.Buttons(Playing with { IsOn = false }), b => Assert.True(b.IsHidden));
    }

    [Fact]
    public void Progress_is_the_position_of_the_length_in_milliseconds()
    {
        var progress = TaskbarToolbarState.Progress(true, Loaded, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(200));

        Assert.Equal(new TaskbarProgress(30_000, 200_000), progress);
    }

    [Fact]
    public void Progress_stays_within_the_track()
    {
        Assert.Equal(new TaskbarProgress(0, 200_000),
            TaskbarToolbarState.Progress(true, Loaded, TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(200)));
        Assert.Equal(new TaskbarProgress(200_000, 200_000),
            TaskbarToolbarState.Progress(true, Loaded, TimeSpan.FromSeconds(201), TimeSpan.FromSeconds(200)));
    }

    [Fact]
    public void No_progress_off_without_a_track_or_without_a_length()
    {
        var at = TimeSpan.FromSeconds(30);
        var length = TimeSpan.FromSeconds(200);

        Assert.True(TaskbarToolbarState.Progress(false, Loaded, at, length).IsNone);
        Assert.True(TaskbarToolbarState.Progress(true, null, at, length).IsNone);
        Assert.True(TaskbarToolbarState.Progress(true, Loaded with { Duration = null }, at, null).IsNone);
        Assert.True(TaskbarToolbarState.Progress(true, Loaded, at, TimeSpan.Zero).IsNone);
    }

    [Fact]
    public void Before_playback_reports_a_length_the_listed_one_holds()
    {
        // A new track has no progress report yet; the bar must not blink off.
        Assert.Equal(new TaskbarProgress(0, 212_000),
            TaskbarToolbarState.Progress(true, Loaded, TimeSpan.Zero, null));
    }

    [Fact]
    public void Gate_passes_about_once_a_second()
    {
        var gate = new TaskbarProgressGate();

        Assert.NotNull(gate.Next(new TaskbarProgress(0, 200_000)));
        Assert.Null(gate.Next(new TaskbarProgress(250, 200_000)));
        Assert.Null(gate.Next(new TaskbarProgress(999, 200_000)));
        Assert.Equal(new TaskbarProgress(1_000, 200_000), gate.Next(new TaskbarProgress(1_000, 200_000)));
    }

    [Fact]
    public void Gate_passes_a_seek_a_new_length_and_none_at_once()
    {
        var gate = new TaskbarProgressGate();
        gate.Next(new TaskbarProgress(10_000, 200_000));

        Assert.NotNull(gate.Next(new TaskbarProgress(5_000, 200_000)));   // seek back
        Assert.NotNull(gate.Next(new TaskbarProgress(5_100, 180_000)));   // another track
        Assert.Equal(TaskbarProgress.None, gate.Next(TaskbarProgress.None));
        Assert.Null(gate.Next(TaskbarProgress.None));
        Assert.NotNull(gate.Next(new TaskbarProgress(0, 180_000)));
    }
}
