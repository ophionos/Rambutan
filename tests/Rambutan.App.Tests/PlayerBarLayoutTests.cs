using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>The player bar's seek line gives way before the side zones do.</summary>
public class PlayerBarLayoutTests
{
    [Fact]
    public void WideBar_KeepsTheFullSeekLine() =>
        Assert.Equal(PlayerBarLayout.MaxSeekWidth, PlayerBarLayout.SeekWidth(1244, 344, 88));

    [Fact]
    public void NarrowBar_ShrinksTheSeekLine_SoTheSidesKeepWhatTheyNeed()
    {
        var seek = PlayerBarLayout.SeekWidth(964, 344, 88);
        Assert.Equal(964 - 2 * 344 - 88, seek);
        Assert.InRange(seek, PlayerBarLayout.MinSeekWidth, PlayerBarLayout.MaxSeekWidth);
    }

    [Fact]
    public void VeryNarrowBar_StopsAtTheFloor() =>
        Assert.Equal(PlayerBarLayout.MinSeekWidth, PlayerBarLayout.SeekWidth(600, 344, 88));

    [Theory]
    [InlineData(40, PlayerBarLayout.MinTitleWidth)]   // a short title keeps the usual room
    [InlineData(270, 270)]                            // a long one takes what it needs
    [InlineData(900, PlayerBarLayout.MaxTitleWidth)]  // up to the cap, then trims
    public void TitleRoom_FollowsTheTitle_BetweenTheFloorAndTheCap(double title, double room) =>
        Assert.Equal(room, PlayerBarLayout.TitleRoom(title));

    [Fact]
    public void ALongTitle_TakesItsRoomFromTheSeekLine()
    {
        // 1404 wide, 96 of artwork and gaps, 102 of actions, 104 of time labels.
        var usual = PlayerBarLayout.SeekWidth(1404, 96 + PlayerBarLayout.MinTitleWidth + 102, 104);
        var titled = PlayerBarLayout.SeekWidth(1404, 96 + PlayerBarLayout.TitleRoom(270) + 102, 104);
        Assert.Equal(PlayerBarLayout.MaxSeekWidth, usual);
        Assert.Equal(1404 - 2 * (96 + 270 + 102) - 104, titled);
    }

    [Fact]
    public void MinBarWidth_IsWhereTheSeekLineReachesItsFloor()
    {
        var min = PlayerBarLayout.MinBarWidth(344, 88);
        Assert.Equal(2 * 344 + 88 + PlayerBarLayout.MinSeekWidth, min);
        Assert.Equal(PlayerBarLayout.MinSeekWidth, PlayerBarLayout.SeekWidth(min, 344, 88));
    }

    [Theory]
    [InlineData(1244, nameof(PlayerBarFit.Full))]
    [InlineData(2 * 344 + 88 + 160, nameof(PlayerBarFit.Full))]       // the full bar at its floor
    [InlineData(2 * 344 + 88 + 159, nameof(PlayerBarFit.Compact))]
    [InlineData(2 * 212 + 88 + 160, nameof(PlayerBarFit.Compact))]    // the compact bar at its floor
    [InlineData(2 * 212 + 88 + 159, nameof(PlayerBarFit.Narrow))]
    [InlineData(300, nameof(PlayerBarFit.Narrow))]
    public void Fit_FullThenCompactThenNarrow(double barWidth, string expected) =>
        Assert.Equal(expected, PlayerBarLayout.Fit(barWidth, 344, 212, 88).ToString());

    [Theory]
    [InlineData(1400, nameof(PlayerBarFit.Full))]
    [InlineData(800, nameof(PlayerBarFit.Compact))]
    [InlineData(560, nameof(PlayerBarFit.Narrow))]
    public void TheBar_FitsTheWindow_AtAnyWidth(double windowWidth, string fit) => Headless.Run(() =>
    {
        var expected = Enum.Parse<PlayerBarFit>(fit);
        ShellResources.Ensure();
        var vm = new MainViewModel();
        vm.Player.ShownTrack = new TrackInfo { VideoId = "a", Title = vm.Player.Title, Artists = [] };
        var window = new MainWindow { DataContext = vm, Width = windowWidth };
        window.Show();
        Settle(window);

        Assert.Equal(expected, window.BarFit);
        // The window keeps the width it was given: the layout never holds it wider.
        Assert.Equal(windowWidth, window.Bounds.Width, 0.5);

        var grid = Named<Grid>(window, "PlayerBarGrid");
        var seek = Named<Grid>(window, "SeekRow");
        var modes = Named<StackPanel>(window, "BarModes");
        var actions = Named<StackPanel>(window, "BarIdentityActions");
        // The right zone starts after the seek line ends and ends inside the bar;
        // the identity's actions end before the seek line starts.
        var seekLeft = seek.TranslatePoint(default, grid)!.Value.X;
        var seekRight = seek.TranslatePoint(new Point(seek.Bounds.Width, 0), grid)!.Value.X;
        var modesLeft = modes.TranslatePoint(default, grid)!.Value.X;
        var actionsRight = actions.TranslatePoint(new Point(actions.Bounds.Width, 0), grid)!.Value.X;
        Assert.True(modesLeft >= seekRight - 0.5, $"the right zone starts at {modesLeft}, the seek line ends at {seekRight}");
        Assert.True(modesLeft + modes.Bounds.Width <= grid.Bounds.Width + 0.5, "the right zone runs past the bar");
        Assert.True(actionsRight <= seekLeft + 0.5, $"the actions end at {actionsRight}, the seek line starts at {seekLeft}");

        var compact = expected != PlayerBarFit.Full;
        Assert.Equal(!compact, Named<StackPanel>(window, "BarFullActions").IsVisible);
        Assert.Equal(compact, Named<Button>(window, "BarMoreButton").IsVisible);
        Assert.Equal(!compact, Named<Slider>(window, "VolumeSlider").IsVisible);
        Assert.Equal(compact, Named<Button>(window, "VolumeButton").IsVisible);
        // Narrow keeps the times, under the line.
        Assert.True(seek.Children[0].IsVisible && seek.Children[2].IsVisible);
        Assert.Equal(expected == PlayerBarFit.Narrow ? 1 : 0, Grid.GetRow((Control)seek.Children[0]));
        window.Close();
    });

    [Fact]
    public void TheMinimum_IsTheCompactBar_AndHoldsOnlyWhileTheWindowIsThatWide() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var vm = new MainViewModel();
        vm.Player.ShownTrack = new TrackInfo { VideoId = "a", Title = vm.Player.Title, Artists = [] };
        var window = new MainWindow { DataContext = vm, Width = 1400 };
        window.Show();
        Settle(window);

        Assert.InRange(window.BarMinWidth, 500, 900);
        Assert.Equal(window.BarMinWidth, window.MinWidth);

        window.Close();
    });

    [Theory]
    [InlineData(1400, 736)]
    [InlineData(736, 736)]
    [InlineData(735.6, 736)]
    [InlineData(700, 0)]   // a tiling window manager made it narrower anyway
    public void TheMinimum_HoldsOnlyWhileTheWindowIsThatWide(double windowWidth, double expected) =>
        Assert.Equal(expected, PlayerBarLayout.HeldMinimum(windowWidth, 736));

    [Fact]
    public void TheFirstTrack_DoesNotChangeTheFitOrTheMinimum() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 800 };
        window.Show();
        Settle(window);
        var (fit, min) = (window.BarFit, window.BarMinWidth);

        // Nothing loaded hides the identity; it still counts toward the fit.
        vm.Player.ShownTrack = new TrackInfo { VideoId = "a", Title = vm.Player.Title, Artists = [] };
        Settle(window);

        Assert.Equal(fit, window.BarFit);
        Assert.Equal(min, window.BarMinWidth);
        window.Close();
    });

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++) // the minimum is set after a layout pass
        {
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }
}
