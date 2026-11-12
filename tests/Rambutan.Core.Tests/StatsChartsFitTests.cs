using Rambutan.Core.Layout;

namespace Rambutan.Core.Tests;

/// <summary>Stats' two charts go side by side only when each half is wide
/// enough; below that they stay stacked.</summary>
public class StatsChartsFitTests
{
    [Fact]
    public void TheChartsGoSideBySide_FromTwoMinimumHalvesAndTheGap()
    {
        Assert.Equal(1824, StatsChartsFit.MinWidth);
        Assert.False(StatsChartsFit.SideBySide(StatsChartsFit.MinWidth - 1));
        Assert.True(StatsChartsFit.SideBySide(StatsChartsFit.MinWidth));
        Assert.Equal(StatsChartsFit.MinHalf, StatsChartsFit.Half(StatsChartsFit.MinWidth));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1200, false)]
    // A 1920 px window's pane beside the expanded sidebar.
    [InlineData(1600, false)]
    // A 2560 px window's, and a 3440 px one's.
    [InlineData(2240, true)]
    [InlineData(3120, true)]
    [InlineData(double.PositiveInfinity, false)]
    public void NarrowerPanesStayStacked(double width, bool sideBySide) =>
        Assert.Equal(sideBySide, StatsChartsFit.SideBySide(width));

    [Fact]
    public void TheHalvesAndTheGapFillTheWidth()
    {
        var half = StatsChartsFit.Half(3120);

        Assert.Equal(3120, 2 * half + StatsChartsFit.Gap);
    }
}
