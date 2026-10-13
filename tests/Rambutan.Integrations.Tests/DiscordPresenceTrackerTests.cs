using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Integrations.Discord;

namespace Rambutan.Integrations.Tests;

public class DiscordPresenceTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DiscordPresenceOptions All = new();

    private static TrackInfo Track(string? thumb = "https://lh3.googleusercontent.com/abc=w544-h544") => new()
    {
        VideoId = "v1",
        Title = "Get Lucky",
        Artists = [new ArtistRef("Daft Punk"), new ArtistRef("Pharrell Williams")],
        Album = "Random Access Memories",
        Duration = TimeSpan.FromSeconds(248),
        ThumbnailUrl = thumb,
    };

    private static DiscordPresenceTracker Playing(TimeSpan position, DateTimeOffset now)
    {
        var tracker = new DiscordPresenceTracker();
        tracker.OnNowPlaying(Track());
        tracker.OnState(PlaybackState.Loading, now);
        tracker.OnState(PlaybackState.Playing, now);
        tracker.OnProgress(position, TimeSpan.FromSeconds(248), now);
        return tracker;
    }

    [Fact]
    public void Playing_ShowsTitleArtistArtworkAndElapsedRemaining()
    {
        var tracker = Playing(TimeSpan.FromSeconds(30), T0);

        var activity = tracker.Desired(T0, All)!;

        Assert.Equal(2, activity.Type);
        Assert.Equal("Get Lucky", activity.Details);
        Assert.Equal("Daft Punk, Pharrell Williams", activity.State);
        Assert.Equal("https://lh3.googleusercontent.com/abc=w544-h544", activity.Assets!.LargeImage);
        Assert.Equal("Random Access Memories", activity.Assets.LargeText);
        var start = T0.AddSeconds(-30).ToUnixTimeMilliseconds();
        Assert.Equal(new DiscordTimestamps(start, start + 248_000), activity.Timestamps);
    }

    [Fact]
    public void NothingPlaying_OrStopped_OrFaulted_ClearsThePresence()
    {
        var tracker = new DiscordPresenceTracker();
        Assert.Null(tracker.Desired(T0, All));

        tracker = Playing(TimeSpan.Zero, T0);
        tracker.OnState(PlaybackState.Stopped, T0);
        Assert.Null(tracker.Desired(T0, All));

        tracker = Playing(TimeSpan.Zero, T0);
        tracker.OnState(PlaybackState.Faulted, T0);
        Assert.Null(tracker.Desired(T0, All));

        tracker = Playing(TimeSpan.Zero, T0);
        tracker.OnNowPlaying(null);
        Assert.Null(tracker.Desired(T0, All));
    }

    [Fact]
    public void OrdinaryTicks_KeepTheSameTimestamps_SoNothingIsResent()
    {
        var tracker = Playing(TimeSpan.FromSeconds(10), T0);
        var before = tracker.Desired(T0, All);

        // Position reports lag or lead wall time by a few hundred ms.
        tracker.OnProgress(TimeSpan.FromSeconds(11.4), null, T0.AddSeconds(1));
        tracker.OnProgress(TimeSpan.FromSeconds(11.7), null, T0.AddSeconds(2));

        Assert.Equal(before, tracker.Desired(T0.AddSeconds(2), All));
    }

    [Fact]
    public void ASeek_MovesTheTimestamps()
    {
        var tracker = Playing(TimeSpan.FromSeconds(10), T0);

        tracker.OnProgress(TimeSpan.FromSeconds(120), null, T0.AddSeconds(1));

        var start = T0.AddSeconds(1 - 120).ToUnixTimeMilliseconds();
        Assert.Equal(start, tracker.Desired(T0.AddSeconds(1), All)!.Timestamps!.Start);

        tracker.OnProgress(TimeSpan.FromSeconds(5), null, T0.AddSeconds(2)); // backwards too
        Assert.Equal(T0.AddSeconds(2 - 5).ToUnixTimeMilliseconds(), tracker.Desired(T0.AddSeconds(2), All)!.Timestamps!.Start);
    }

    [Fact]
    public void Paused_ClearsThePresenceAtOnce()
    {
        // Discord has no paused state: without timestamps it counts up from the
        // update, so a paused card would read as playing.
        var tracker = Playing(TimeSpan.FromSeconds(40), T0);
        var pausedAt = T0.AddSeconds(5);

        tracker.OnState(PlaybackState.Paused, pausedAt);

        Assert.Null(tracker.Desired(pausedAt, All));
        Assert.Null(tracker.Desired(pausedAt, All with { ShowElapsed = false }));
    }

    [Fact]
    public void Resuming_ShowsTheTrackAgain_WithTheClockAnchoredAtThePausedPosition()
    {
        var tracker = Playing(TimeSpan.FromSeconds(40), T0);
        tracker.OnState(PlaybackState.Paused, T0);
        var resumeAt = T0.AddMinutes(5);

        tracker.OnState(PlaybackState.Playing, resumeAt);

        var activity = tracker.Desired(resumeAt, All)!;
        Assert.Equal("Get Lucky", activity.Details);
        Assert.Equal(resumeAt.AddSeconds(-40).ToUnixTimeMilliseconds(), activity.Timestamps!.Start);
    }

    [Fact]
    public void Loading_ShowsTheNewTrackWithoutAClock()
    {
        var tracker = Playing(TimeSpan.FromSeconds(100), T0);
        tracker.OnNowPlaying(Track() with { Title = "Next One" });
        tracker.OnState(PlaybackState.Loading, T0);

        var activity = tracker.Desired(T0, All)!;
        Assert.Equal("Next One", activity.Details);
        Assert.Null(activity.Timestamps);

        tracker.OnState(PlaybackState.Playing, T0.AddSeconds(1));
        Assert.Equal(T0.AddSeconds(1).ToUnixTimeMilliseconds(), tracker.Desired(T0.AddSeconds(1), All)!.Timestamps!.Start);
    }

    [Fact]
    public void UnknownDuration_ShowsElapsedOnly()
    {
        var tracker = new DiscordPresenceTracker();
        tracker.OnNowPlaying(Track() with { Duration = null });
        tracker.OnState(PlaybackState.Playing, T0);

        Assert.Equal(new DiscordTimestamps(T0.ToUnixTimeMilliseconds(), null), tracker.Desired(T0, All)!.Timestamps);
    }

    [Fact]
    public void Toggles_LeaveOutEachElement()
    {
        var tracker = Playing(TimeSpan.FromSeconds(30), T0);

        var none = tracker.Desired(T0, new DiscordPresenceOptions(ShowTrack: false, ShowArtwork: false, ShowElapsed: false))!;

        Assert.Equal(2, none.Type);
        Assert.Null(none.Details);
        Assert.Null(none.State);
        Assert.Null(none.Timestamps);
        Assert.Equal(new DiscordAssets(DiscordPresenceTracker.FallbackImageKey, null), none.Assets);
    }

    [Fact]
    public void ChangingAToggle_ChangesTheActivity_SoItIsResent()
    {
        var tracker = Playing(TimeSpan.FromSeconds(30), T0);

        Assert.NotEqual(tracker.Desired(T0, All), tracker.Desired(T0, All with { ShowElapsed = false }));
        Assert.Equal(tracker.Desired(T0, All), tracker.Desired(T0, new DiscordPresenceOptions()));
    }

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/x=w120", "https://lh3.googleusercontent.com/x=w120")]
    [InlineData("http://example.com/x.jpg", "rambutan")]
    [InlineData("not a url", "rambutan")]
    [InlineData(null, "rambutan")]
    public void ImageKey_UsesHttpsArtworkElseTheLogo(string? thumb, string expected) =>
        Assert.Equal(expected, DiscordPresenceTracker.ImageKey(thumb));

    [Fact]
    public void ImageKey_TooLongForDiscord_FallsBackToTheLogo() =>
        Assert.Equal("rambutan", DiscordPresenceTracker.ImageKey("https://example.com/" + new string('a', 300)));

    [Fact]
    public void Fit_TrimsPadsAndTruncatesToDiscordsLimits()
    {
        Assert.Null(DiscordPresenceTracker.Fit("  "));
        Assert.Null(DiscordPresenceTracker.Fit(null));
        Assert.Equal("X⠀", DiscordPresenceTracker.Fit("X"));
        Assert.Equal("Song", DiscordPresenceTracker.Fit(" Song "));

        var fitted = DiscordPresenceTracker.Fit(new string('a', 200))!;
        Assert.Equal(DiscordPresenceTracker.MaxText, fitted.Length);
        Assert.EndsWith("…", fitted);
    }

    [Fact]
    public void Fit_NeverSplitsASurrogatePair()
    {
        var text = new string('a', 126) + "😀😀😀";

        var fitted = DiscordPresenceTracker.Fit(text)!;

        Assert.True(fitted.Length <= DiscordPresenceTracker.MaxText);
        Assert.Equal(new string('a', 126) + "…", fitted);
    }

    [Fact]
    public void Backoff_StartsQuick_SettlesAtAMinute_AndResets()
    {
        var backoff = new DiscordReconnectBackoff();

        var delays = Enumerable.Range(0, 8).Select(_ => backoff.Next().TotalSeconds).ToArray();

        Assert.Equal([2, 5, 10, 20, 30, 60, 60, 60], delays);
        backoff.Reset();
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.Next());
    }

    [Fact]
    public void Budget_AllowsFivePerTwentySeconds_ThenSaysHowLongToWait()
    {
        var budget = new DiscordSendBudget();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(TimeSpan.Zero, budget.WaitBefore(T0.AddSeconds(i)));
            budget.Record(T0.AddSeconds(i));
        }

        Assert.Equal(TimeSpan.FromSeconds(15), budget.WaitBefore(T0.AddSeconds(5)));
        Assert.Equal(TimeSpan.Zero, budget.WaitBefore(T0.AddSeconds(20)));
    }
}
