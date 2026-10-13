using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>The player bar's two time slots either side of the transport.</summary>
public class PlayerBarTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(4, "0:04")]
    [InlineData(334, "5:34")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(4 * 3600 + 5 * 60 + 6, "4:05:06")]
    public void FormatTime_MinutesUnderAnHour_HoursFromOne(int seconds, string expected) =>
        Assert.Equal(expected, PlayerBarViewModel.FormatTime(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void FormatTime_Negative_ReadsAsZero() =>
        Assert.Equal("0:00", PlayerBarViewModel.FormatTime(TimeSpan.FromSeconds(-1)));

    [Fact]
    public void ResetProgress_NewTrack_StartsFromZeroWithItsOwnLength()
    {
        Headless.Run(() =>
        {
            var player = new PlayerBarViewModel(null!, new QueueModel(), null)
            {
                // The previous track's numbers, as the last progress report left them.
                PositionSeconds = 25,
                DurationSeconds = 334,
                BufferedFraction = 0.4,
                ElapsedDisplay = "0:25",
                DurationDisplay = "5:34",
            };

            player.ResetProgress(new TrackInfo
            {
                VideoId = "v2", Title = "Next", Artists = [], Duration = TimeSpan.FromSeconds(210),
            });

            Assert.Equal(0, player.PositionSeconds);
            Assert.Equal(210, player.DurationSeconds);
            Assert.Equal(0, player.BufferedFraction);
            Assert.Equal("0:00", player.ElapsedDisplay);
            Assert.Equal("3:30", player.DurationDisplay);
        });
    }

    [Fact]
    public void ResetProgress_NothingPlaying_ShowsZeroes()
    {
        Headless.Run(() =>
        {
            var player = new PlayerBarViewModel(null!, new QueueModel(), null)
            {
                PositionSeconds = 25,
                ElapsedDisplay = "0:25",
                DurationDisplay = "5:34",
            };

            player.ResetProgress(null);

            Assert.Equal(0, player.PositionSeconds);
            Assert.Equal("0:00", player.ElapsedDisplay);
            Assert.Equal("0:00", player.DurationDisplay);
        });
    }

    [Fact]
    public void ResetProgress_WhileDraggingTheSeekThumb_LeavesTheThumbAlone()
    {
        Headless.Run(() =>
        {
            var player = new PlayerBarViewModel(null!, new QueueModel(), null)
            {
                PositionSeconds = 90,
                IsSeeking = true,
            };

            player.ResetProgress(new TrackInfo { VideoId = "v2", Title = "Next", Artists = [] });

            Assert.Equal(90, player.PositionSeconds);
            Assert.Equal("0:00", player.ElapsedDisplay);
        });
    }

    private static TrackInfo Track(string id, int seconds) => new()
    {
        VideoId = id, Title = $"Title {id}", Artists = [], Duration = TimeSpan.FromSeconds(seconds),
    };

    [Fact]
    public void RestoredQueue_ShowsItsCurrentTrackCuedFromZero()
    {
        Headless.Run(() =>
        {
            var queue = new QueueModel();
            queue.SetQueue([Track("a", 60), Track("b", 190)], startIndex: 1);

            var player = new PlayerBarViewModel(null!, queue, null);

            Assert.Equal("b", player.ShownTrack?.VideoId);
            Assert.Null(player.NowPlaying);
            Assert.Equal("Title b", player.Title);
            Assert.Equal("0:00", player.ElapsedDisplay);
            Assert.Equal("3:10", player.DurationDisplay);
        });
    }

    [Fact]
    public void QueueFilledAfterStartup_IsShownCued_AndClearedQueueIsNothingPlaying()
    {
        Headless.Run(() =>
        {
            var queue = new QueueModel();
            var player = new PlayerBarViewModel(null!, queue, null);
            Assert.Null(player.ShownTrack);

            queue.SetQueue([Track("a", 60)]);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("a", player.ShownTrack?.VideoId);

            queue.SetQueue([]);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Null(player.ShownTrack);
            Assert.Equal(Rambutan.App.Localization.Strings.Get("player.nothingPlaying"), player.Title);
        });
    }

    [Fact]
    public void RepeatAndShuffleSetOnTheQueue_ShowOnTheButtons()
    {
        Headless.Run(() =>
        {
            // As the OS media session (MPRIS) sets them: on the queue, not through the bar.
            var queue = new QueueModel();
            queue.SetQueue([Track("a", 60), Track("b", 190)]);
            var player = new PlayerBarViewModel(null!, queue, null);

            queue.Repeat = RepeatMode.One;
            queue.SetShuffle(true);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.True(player.IsRepeatOne);
            Assert.True(player.IsShuffleOn);
        });
    }
}
