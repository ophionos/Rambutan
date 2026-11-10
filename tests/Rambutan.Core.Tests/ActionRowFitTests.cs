using Rambutan.Core.Layout;

namespace Rambutan.Core.Tests;

/// <summary>A page's action row keeps the actions that fit and moves the rest
/// into its More menu.</summary>
public class ActionRowFitTests
{
    // A hero row: Play all (56) pinned, then four outlined icons (36), 12 apart.
    private static readonly double[] Icons = [36, 36, 36, 36];
    private const double Play = 56;
    private const double Gap = 12;
    private const double More = 36;

    private static int Kept(double available) => ActionRowFit.Kept(available, Play, 1, Icons, Gap, More);

    [Fact]
    public void EverythingFits_KeepsEveryAction()
    {
        // 56 + 4 * 36 + 4 gaps of 12 = 248.
        Assert.Equal(4, Kept(248));
        Assert.Equal(4, Kept(1000));
    }

    [Fact]
    public void OnePixelShort_MovesTheLastAction_AndLeavesRoomForMore()
    {
        // Three icons and More take the same room as four icons, so one more
        // has to go: 56 + 2 * 36 + 36 + 3 * 12 = 200.
        Assert.Equal(2, Kept(247));
        Assert.Equal(2, Kept(200));
        Assert.Equal(1, Kept(199));
    }

    [Fact]
    public void ASingleAction_StaysWhileItFits_ThenMoreTakesItsPlace()
    {
        Assert.Equal(1, ActionRowFit.Kept(104, Play, 1, [36], Gap, More));
        Assert.Equal(0, ActionRowFit.Kept(103, Play, 1, [36], Gap, More));
    }

    [Fact]
    public void TooNarrowForAnything_KeepsOnlyThePinnedItems()
    {
        Assert.Equal(0, Kept(104));
        Assert.Equal(0, Kept(10));
        Assert.Equal(0, Kept(0));
    }

    [Fact]
    public void ActionsMoveFromTheEnd_WhateverTheirWidths()
    {
        // A narrow last action doesn't stay in place of a wide earlier one.
        double[] actions = [120, 90, 30];
        // The first two and More (40) are wider than all three, so one pixel
        // short of everything leaves only the first.
        Assert.Equal(3, ActionRowFit.Kept(100 + 240 + 3 * 10, 100, 1, actions, 10, 40));
        Assert.Equal(1, ActionRowFit.Kept(100 + 240 + 3 * 10 - 1, 100, 1, actions, 10, 40));
        Assert.Equal(1, ActionRowFit.Kept(100 + 120 + 40 + 2 * 10, 100, 1, actions, 10, 40));
        Assert.Equal(0, ActionRowFit.Kept(100 + 120 + 40 + 2 * 10 - 1, 100, 1, actions, 10, 40));
    }

    [Fact]
    public void NoPinnedItem_CountsNoGapBeforeTheFirstAction()
    {
        Assert.Equal(2, ActionRowFit.Kept(82, 0, 0, [36, 36], 10, 36));
        Assert.Equal(0, ActionRowFit.Kept(81, 0, 0, [36, 36], 10, 36));
        Assert.Equal(1, ActionRowFit.Kept(82, 0, 0, [36, 36, 36], 10, 36));
    }

    [Fact]
    public void NoActions_KeepsNone() =>
        Assert.Equal(0, ActionRowFit.Kept(10, 200, 2, [], 10, 36));

    [Fact]
    public void LayoutRounding_DoesNotMoveAnAction() =>
        Assert.Equal(4, Kept(247.6));
}
