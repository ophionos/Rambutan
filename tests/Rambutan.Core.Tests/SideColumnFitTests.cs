using Rambutan.Core.Layout;

namespace Rambutan.Core.Tests;

/// <summary>A side column shows only where the pane holds the main column, the
/// gap and the side column at its narrowest.</summary>
public class SideColumnFitTests
{
    private const double Main = 1130;

    [Fact]
    public void BelowTheThreshold_ThereIsNoSideColumn_AndTheMainColumnHasThePane()
    {
        var fit = SideColumnFit.For(SideColumnFit.MinPane(Main) - 1, Main);

        Assert.False(fit.Show);
        Assert.Equal(SideColumnFit.MinPane(Main) - 1, fit.Main);
        Assert.Equal(0, fit.Side);
    }

    [Fact]
    public void AtTheThreshold_TheSideColumnIsAtItsNarrowest()
    {
        var fit = SideColumnFit.For(SideColumnFit.MinPane(Main), Main);

        Assert.True(fit.Show);
        Assert.Equal(Main, fit.Main);
        Assert.Equal(SideColumnFit.MinSide, fit.Side);
        Assert.Equal(1720, SideColumnFit.MinPane(Main));
    }

    [Theory]
    [InlineData(1720)]
    [InlineData(2280)]
    [InlineData(3170)]
    [InlineData(5000)]
    public void AWidePane_KeepsTheMainColumn_AndGivesTheSideColumnTheRest(double pane)
    {
        var fit = SideColumnFit.For(pane, Main);

        Assert.True(fit.Show);
        Assert.Equal(Main, fit.Main);
        Assert.Equal(pane, fit.Main + SideColumnFit.Gap + fit.Side);
    }

    [Fact]
    public void TheNarrowestSideColumn_HoldsThreeCards() =>
        Assert.True(SideColumnFit.MinSide >= 3 * 176 + 2 * 4);

    [Theory]
    [InlineData(0)]
    [InlineData(-20)]
    public void APaneWithNoWidth_HasNoColumnWidth(double pane)
    {
        var fit = SideColumnFit.For(pane, Main);

        Assert.False(fit.Show);
        Assert.Equal(0, fit.Main);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void AnUnmeasuredPane_HasNoSideColumn(double pane)
    {
        var fit = SideColumnFit.For(pane, Main);

        Assert.False(fit.Show);
        Assert.Equal(Main, fit.Main);
    }
}
