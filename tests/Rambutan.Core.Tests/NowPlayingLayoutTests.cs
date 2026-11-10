using Rambutan.Core.Layout;

namespace Rambutan.Core.Tests;

/// <summary>The Now Playing artwork's size, and the title's, from the room the stage gives.</summary>
public class NowPlayingLayoutTests
{
    /// <summary>A title room like the view's: the gap under the artwork, one
    /// line of title and the artist line.</summary>
    private static double OneLine(double titleSize) => 28 + 1.3 * titleSize + 6 + 20;

    /// <summary>The same with the title on two lines.</summary>
    private static double TwoLines(double titleSize) => 28 + 2 * 1.3 * titleSize + 6 + 20;

    [Fact]
    public void TheDefaultWindow_GivesArtworkClearlyLargerThanBefore()
    {
        // About 1092 x 590 of stage at the default window size.
        var fit = NowPlayingLayout.Fit(1092, 590, 0, fullscreen: false, OneLine);
        Assert.InRange(fit.Art, 360, 420);
        Assert.InRange(fit.TitleSize, 28, 32);
    }

    [Fact]
    public void ArtSize_IsAShareOfTheRoom() =>
        Assert.Equal(NowPlayingLayout.Share * 400, NowPlayingLayout.ArtSize(400, 0, fullscreen: false), 6);

    [Fact]
    public void ArtSize_LeavesTheRingRoomOnEachSide() =>
        Assert.Equal(NowPlayingLayout.Share * 500 - 100, NowPlayingLayout.ArtSize(500, 50, fullscreen: false), 6);

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-40)]
    public void ArtSize_StopsAtTheFloor(double room) =>
        Assert.Equal(NowPlayingLayout.MinArt, NowPlayingLayout.ArtSize(room, 0, fullscreen: false));

    [Fact]
    public void ArtSize_StopsAtTheCap_Windowed() =>
        Assert.Equal(NowPlayingLayout.MaxArt, NowPlayingLayout.ArtSize(2000, 0, fullscreen: false));

    [Fact]
    public void ArtSize_HasALargerCap_InFullscreen()
    {
        Assert.Equal(NowPlayingLayout.FullscreenMaxArt, NowPlayingLayout.ArtSize(2000, 0, fullscreen: true));
        Assert.True(NowPlayingLayout.FullscreenMaxArt > NowPlayingLayout.MaxArt);
        // Between the caps, fullscreen follows the room.
        Assert.Equal(NowPlayingLayout.Share * 700, NowPlayingLayout.ArtSize(700, 0, fullscreen: true), 6);
    }

    [Fact]
    public void ANarrowStage_LimitsTheArtworkByItsWidth()
    {
        var fit = NowPlayingLayout.Fit(300, 1000, 0, fullscreen: false, OneLine);
        Assert.Equal(NowPlayingLayout.Share * 300, fit.Art, 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(50.0)]
    public void TheTitleAndArtist_StayInTheStage_AtEveryHeight(double ring)
    {
        foreach (var room in new Func<double, double>[] { OneLine, TwoLines })
        {
            for (var height = 280.0; height <= 1600; height += 10)
            {
                foreach (var fullscreen in new[] { false, true })
                {
                    var fit = NowPlayingLayout.Fit(2000, height, ring, fullscreen, room);
                    // Only the floor may overflow, and without the ring the
                    // smallest stage still holds it.
                    if (fit.Art <= NowPlayingLayout.MinArt && ring > 0)
                        continue;
                    var used = fit.Art + 2 * ring + room(fit.TitleSize);
                    Assert.True(used <= height + 0.001,
                        $"ring {ring}, height {height}: artwork {fit.Art} and title {fit.TitleSize} take {used}");
                }
            }
        }
    }

    [Fact]
    public void TheSmallestStage_HoldsTheArtworkAndTitle()
    {
        var fit = NowPlayingLayout.Fit(560, 280, 0, fullscreen: false, TwoLines);
        Assert.True(fit.Art + TwoLines(fit.TitleSize) <= 280, $"{fit.Art} + {TwoLines(fit.TitleSize)}");
        Assert.Equal(24, fit.TitleSize);
    }

    [Fact]
    public void TheTitle_FollowsTheArtwork()
    {
        var fit = NowPlayingLayout.Fit(2000, 900, 0, fullscreen: false, OneLine);
        Assert.Equal(NowPlayingLayout.MaxArt, fit.Art);
        Assert.Equal(32, fit.TitleSize);
    }

    [Theory]
    [InlineData(120, 24)]
    [InlineData(299, 24)]
    [InlineData(300, 24)]
    [InlineData(360, 28)]
    [InlineData(390, 30)]
    [InlineData(420, 32)]
    [InlineData(640, 32)]
    public void TitleSize_GrowsWithTheArtwork_BetweenTwoSizes(double art, double size) =>
        Assert.Equal(size, NowPlayingLayout.TitleSize(art));

    [Fact]
    public void TitleSize_IsAWholeNumber() =>
        Assert.Equal(Math.Round(NowPlayingLayout.TitleSize(333)), NowPlayingLayout.TitleSize(333));

    [Fact]
    public void TheHeaderCover_IsSmall() =>
        Assert.InRange(NowPlayingLayout.HeaderArt, 56, 72);

    [Fact]
    public void TheDecodeWidth_CoversTheLargestArtwork_AtOneAndAHalfTimes() =>
        Assert.True(NowPlayingLayout.DecodeWidth >= NowPlayingLayout.FullscreenMaxArt * 1.5);
}
