using Rambutan.Core.Layout;

namespace Rambutan.Core.Tests;

/// <summary>A track row's album column shows only where the title keeps its
/// room, grows with the list, and stops at a cap.</summary>
public class TrackRowColumnsTests
{
    private const double Fixed = 318;

    /// <summary>The narrowest list that shows the column.</summary>
    private const double Threshold = Fixed + TrackRowColumns.MinTitle + TrackRowColumns.MinAlbum;

    [Theory]
    [InlineData(0)]
    [InlineData(400)]
    [InlineData(Threshold - 1)]
    [InlineData(Threshold - 0.5)]
    public void BelowTheThreshold_TheColumnDoesNotShow(double width)
    {
        Assert.Equal(0, TrackRowColumns.AlbumWidth(width, Fixed));
    }

    [Fact]
    public void AtTheThreshold_TheColumnIsItsMinimum()
    {
        Assert.Equal(TrackRowColumns.MinAlbum, TrackRowColumns.AlbumWidth(Threshold, Fixed));
    }

    [Fact]
    public void TheColumn_GrowsWithTheWidth()
    {
        var narrow = TrackRowColumns.AlbumWidth(Threshold + 40, Fixed);
        var wide = TrackRowColumns.AlbumWidth(Threshold + 120, Fixed);

        Assert.True(narrow > TrackRowColumns.MinAlbum);
        Assert.True(wide > narrow);
    }

    [Theory]
    [InlineData(1400)]
    [InlineData(2000)]
    [InlineData(4000)]
    public void TheColumn_StopsAtItsCap(double width)
    {
        Assert.Equal(TrackRowColumns.MaxAlbum, TrackRowColumns.AlbumWidth(width, Fixed));
    }

    [Fact]
    public void TheColumn_IsNeverWiderThanAThirdOfTheList()
    {
        // A small fixed part leaves the title plenty: 30 % of the list is the limit.
        Assert.Equal(180, TrackRowColumns.AlbumWidth(600, 40), 6);
    }

    [Fact]
    public void TheTitle_NeverHasLessThanItsMinimum_WhileTheColumnShows()
    {
        foreach (var fixedWidth in new double[] { 0, 120, 266, 318, 600 })
        {
            for (double width = 0; width <= 3000; width += 0.5)
            {
                var album = TrackRowColumns.AlbumWidth(width, fixedWidth);
                if (album <= 0)
                    continue;
                Assert.True(width - fixedWidth - album >= TrackRowColumns.MinTitle,
                    $"list {width}, fixed {fixedWidth}: album {album}");
                Assert.InRange(album, TrackRowColumns.MinAlbum, TrackRowColumns.MaxAlbum);
            }
        }
    }

    [Theory]
    [InlineData(double.NaN, 318)]
    [InlineData(0, 318)]
    [InlineData(-10, 318)]
    [InlineData(double.PositiveInfinity, 318)]
    [InlineData(1200, double.NaN)]
    public void AWidthThatIsNotOne_HidesTheColumn(double width, double fixedWidth)
    {
        Assert.Equal(0, TrackRowColumns.AlbumWidth(width, fixedWidth));
    }
}
