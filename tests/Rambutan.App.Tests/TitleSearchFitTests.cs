using Rambutan.App.Views;

namespace Rambutan.App.Tests;

/// <summary>Where the title bar's search field goes: centred on the bar where it
/// can be, between the navigation buttons and the caption buttons' reserve.</summary>
public class TitleSearchFitTests
{
    // The title bar's own items end here at the default text size.
    private const double NavEnd = 340;

    [Fact]
    public void AWideBar_CentresTheFieldAtItsWidest()
    {
        var place = TitleSearchFit.Place(1280, NavEnd);
        Assert.Equal((430d, 420d), place);
    }

    [Fact]
    public void WhenTheCentreWouldOverlapTheButtons_TheFieldStartsAfterThem()
    {
        // Centred, a 420 field in a 1000 bar would start at 290, over the buttons.
        var place = TitleSearchFit.Place(1000, NavEnd);
        Assert.Equal((352d, 420d), place);
    }

    [Fact]
    public void ANarrowerBar_ShrinksTheFieldToTheRoomLeft()
    {
        // Room: from 352 to 800 - 150 = 650, so 298.
        var place = TitleSearchFit.Place(800, NavEnd);
        Assert.Equal((352d, 298d), place);
    }

    [Fact]
    public void TheFieldShows_DownToItsMinimumWidth_AndHidesBelowIt()
    {
        var floor = NavEnd + TitleSearchFit.Gap + TitleSearchFit.MinWidth + TitleSearchFit.CaptionReserve;
        Assert.Equal((352d, 240d), TitleSearchFit.Place(floor, NavEnd));
        Assert.Null(TitleSearchFit.Place(floor - 1, NavEnd));
    }

    [Fact]
    public void ABarNotLaidOutYet_HidesTheField() =>
        Assert.Null(TitleSearchFit.Place(0, 0));
}
