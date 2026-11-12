using Rambutan.Core.Layout;

namespace Rambutan.Core.Tests;

/// <summary>The Settings column grows with the pane; the section list stays at
/// the pane's right edge and clear of the column.</summary>
public class SettingsColumnFitTests
{
    [Fact]
    public void ANarrowPane_HasNoList_AndTheColumnTakesThePaneLessItsInsets()
    {
        var fit = SettingsColumnFit.For(800);

        Assert.False(fit.ShowList);
        Assert.Equal(800 - 20, fit.Column);
    }

    [Fact]
    public void BelowTheThreshold_ThereIsNoList_AtTheThreshold_TheColumnIsAtItsMinimum()
    {
        var below = SettingsColumnFit.For(SettingsColumnFit.ListMinPane - 1);
        var at = SettingsColumnFit.For(SettingsColumnFit.ListMinPane);

        Assert.False(below.ShowList);
        Assert.True(at.ShowList);
        Assert.Equal(SettingsColumnFit.MinColumn, at.Column);
        Assert.Equal(968, SettingsColumnFit.ListMinPane);
    }

    [Fact]
    public void A1700Pane_ShowsTheList_AndTheColumnStopsAtTheMaximum()
    {
        var fit = SettingsColumnFit.For(1700);

        Assert.True(fit.ShowList);
        Assert.Equal(1040, fit.Column);
        Assert.Equal(1488, SettingsColumnFit.ListLeft(1700));
    }

    [Fact]
    public void AMidPane_FillsThePaneMinusTheListAndGaps()
    {
        var fit = SettingsColumnFit.For(1200);

        Assert.Equal(1200 - 4 - 32 - 200 - 12, fit.Column);
    }

    [Fact]
    public void AVeryWidePane_HitsTheMaximum()
    {
        Assert.Equal(SettingsColumnFit.MaxColumn, SettingsColumnFit.For(1700).Column);
        Assert.Equal(SettingsColumnFit.MaxColumn, SettingsColumnFit.For(5000).Column);
    }

    [Fact]
    public void AHiddenList_LetsTheColumnGrowToTheMaximumOnly()
    {
        Assert.Equal(0, SettingsColumnFit.For(10).Column);
        Assert.Equal(0, SettingsColumnFit.For(0).Column);
        Assert.True(SettingsColumnFit.For(SettingsColumnFit.ListMinPane - 1).Column <= SettingsColumnFit.MaxColumn);
    }

    [Theory]
    [InlineData(968)]
    [InlineData(1000)]
    [InlineData(1100)]
    [InlineData(1280)]
    [InlineData(1720)]
    [InlineData(2560)]
    [InlineData(5000)]
    public void TheList_NeverOverlapsTheColumn_AndEndsAtThePanesRightInset(double pane)
    {
        var fit = SettingsColumnFit.For(pane);
        var columnRight = SettingsColumnFit.ColumnLeft + fit.Width;
        var listLeft = SettingsColumnFit.ListLeft(pane);

        Assert.True(fit.ShowList);
        Assert.True(listLeft >= columnRight + SettingsColumnFit.Gap, $"list at {listLeft}, column ends {columnRight}");
        Assert.Equal(pane - SettingsColumnFit.RightInset, listLeft + SettingsColumnFit.ListWidth);
    }
}

/// <summary>From a pane with room for two columns at their minimum beside the
/// list, the sections take two; the split keeps them in order and the two
/// columns' heights as near as the sections allow.</summary>
public class SettingsTwoColumnTests
{
    [Fact]
    public void BelowTheThreshold_OneColumn_AtIt_TwoAtTheirMinimum()
    {
        var below = SettingsColumnFit.For(SettingsColumnFit.TwoColumnMinPane - 1);
        var at = SettingsColumnFit.For(SettingsColumnFit.TwoColumnMinPane);

        Assert.Equal(1712, SettingsColumnFit.TwoColumnMinPane);
        Assert.Equal(1, below.Columns);
        Assert.Equal(SettingsColumnFit.MaxColumn, below.Column);
        Assert.Equal(2, at.Columns);
        Assert.Equal(SettingsColumnFit.MinColumn, at.Column);
        Assert.True(at.ShowList);
    }

    [Theory]
    // A 1920 px window's pane beside the expanded sidebar: one column.
    [InlineData(1604, 1, 1040)]
    // A 2560 px window's: two columns that share the room.
    [InlineData(2244, 2, 986)]
    // A 3440 px window's: two columns at the maximum.
    [InlineData(3124, 2, 1040)]
    public void TheColumns_ShareTheRoomBesideTheList_UpToTheMaximum(double pane, int columns, double column)
    {
        var fit = SettingsColumnFit.For(pane);

        Assert.Equal(columns, fit.Columns);
        Assert.Equal(column, fit.Column);
        Assert.Equal(columns * column + (columns - 1) * SettingsColumnFit.ColumnGap, fit.Width);
    }

    [Theory]
    [InlineData(1712)]
    [InlineData(2000)]
    [InlineData(2244)]
    [InlineData(3124)]
    [InlineData(5000)]
    public void TwoColumns_NeverReachTheList(double pane)
    {
        var fit = SettingsColumnFit.For(pane);

        Assert.True(SettingsColumnFit.ColumnLeft + fit.Width + SettingsColumnFit.Gap <= SettingsColumnFit.ListLeft(pane));
    }

    [Fact]
    public void ANarrowPane_AndAPaneWithoutTheList_HaveOneColumn()
    {
        Assert.Equal(1, SettingsColumnFit.For(800).Columns);
        Assert.Equal(1, SettingsColumnFit.For(1200).Columns);
        Assert.Equal(1200 - 4 - 32 - 200 - 12, SettingsColumnFit.For(1200).Width);
    }

    [Theory]
    // Equal sections: half each.
    [InlineData(new double[] { 100, 100, 100, 100 }, 2)]
    // A tall first section stands alone when the rest is nearer its height.
    [InlineData(new double[] { 500, 100, 100, 100 }, 1)]
    // A tall last section takes the second column.
    [InlineData(new double[] { 100, 100, 100, 500 }, 3)]
    // Of two splits equally near, the first column is the taller.
    [InlineData(new double[] { 100, 100, 100 }, 2)]
    // Hidden sections weigh nothing.
    [InlineData(new double[] { 0, 300, 0, 300, 0 }, 3)]
    [InlineData(new double[] { 300 }, 1)]
    [InlineData(new double[] { 0, 300, 0 }, 2)]
    [InlineData(new double[] { }, 0)]
    public void TheSplit_IsWhereTheTwoHeightsAreNearest(double[] heights, int first) =>
        Assert.Equal(first, SettingsColumnFit.Split(heights));

    [Fact]
    public void TheSplit_NeverMakesTheSecondColumnTallerThanItMust()
    {
        double[] heights = [120, 480, 260, 300, 640, 90, 210, 330, 150, 400, 180];
        var first = SettingsColumnFit.Split(heights);

        var a = heights.Take(first).Sum();
        var b = heights.Skip(first).Sum();
        for (var other = 0; other <= heights.Length; other++)
            Assert.True(Math.Abs(a - b) <= Math.Abs(heights.Take(other).Sum() - heights.Skip(other).Sum()));
    }
}
