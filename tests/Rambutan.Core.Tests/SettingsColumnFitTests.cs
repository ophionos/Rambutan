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
    public void A1720Pane_ShowsTheList_AndTheColumnStopsAtTheMaximum()
    {
        var fit = SettingsColumnFit.For(1720);

        Assert.True(fit.ShowList);
        Assert.Equal(1040, fit.Column);
        Assert.Equal(1508, SettingsColumnFit.ListLeft(1720));
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
        Assert.Equal(SettingsColumnFit.MaxColumn, SettingsColumnFit.For(1720).Column);
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
        var columnRight = SettingsColumnFit.ColumnLeft + fit.Column;
        var listLeft = SettingsColumnFit.ListLeft(pane);

        Assert.True(fit.ShowList);
        Assert.True(listLeft >= columnRight + SettingsColumnFit.Gap, $"list at {listLeft}, column ends {columnRight}");
        Assert.Equal(pane - SettingsColumnFit.RightInset, listLeft + SettingsColumnFit.ListWidth);
    }
}
