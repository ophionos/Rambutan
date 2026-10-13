using Avalonia.Controls;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>The compact meter on the player bar's artwork.</summary>
public class BarVisualizerTests
{
    [Fact]
    public void GroupBands_EachBarIsTheLoudestBandInItsSlice()
    {
        // 32 bands, the lower 24 used: six bars of four bands each.
        var bands = new float[32];
        bands[1] = 0.3f;
        bands[2] = 0.8f;   // bar 0
        bands[5] = 0.5f;   // bar 1
        bands[23] = 0.9f;  // bar 5, the last band used

        var bars = new float[6];
        VisualizerControl.GroupBands(bands, bars);

        Assert.Equal([0.8f, 0.5f, 0f, 0f, 0f, 0.9f], bars);
    }

    [Fact]
    public void GroupBands_LeavesTheTopQuarterOut()
    {
        var bands = new float[32];
        for (var i = 24; i < 32; i++)
            bands[i] = 1f;

        var bars = new float[6];
        VisualizerControl.GroupBands(bands, bars);

        Assert.All(bars, level => Assert.Equal(0f, level));
    }

    [Fact]
    public void GroupBands_MoreBarsThanBands_GivesEveryBarABand()
    {
        var bands = new float[8]; // 6 used
        for (var i = 0; i < bands.Length; i++)
            bands[i] = (i + 1) / 10f;

        var bars = new float[10];
        VisualizerControl.GroupBands(bands, bars);

        Assert.All(bars, level => Assert.True(level > 0));
        Assert.All(bars, level => Assert.True(level <= 0.6f + 1e-6));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void CompactBar_IsCentredVertically_AtEveryLevel(float level)
    {
        var bar = VisualizerControl.CompactBar(2, 5, level, new Avalonia.Size(31, 28), gap: 3, minHeight: 2);

        Assert.Equal(14, bar.Center.Y, 6);
        Assert.Equal(System.Math.Max(2, level * 28), bar.Height, 6);
    }

    [Fact]
    public void CompactBar_FiveBars_FillTheWidthSymmetrically()
    {
        var size = new Avalonia.Size(31, 28);
        var first = VisualizerControl.CompactBar(0, 5, 1f, size, gap: 3, minHeight: 2);
        var middle = VisualizerControl.CompactBar(2, 5, 1f, size, gap: 3, minHeight: 2);
        var last = VisualizerControl.CompactBar(4, 5, 1f, size, gap: 3, minHeight: 2);

        Assert.Equal(0, first.Left, 6);
        Assert.Equal(31, last.Right, 6);
        Assert.Equal(15.5, middle.Center.X, 6);
    }

    [Fact]
    public void CompactBar_TooNarrow_IsEmpty()
    {
        var bar = VisualizerControl.CompactBar(0, 5, 1f, new Avalonia.Size(10, 28), gap: 3, minHeight: 2);
        Assert.Equal(0, bar.Width);
    }

    [Theory]
    [InlineData(true, MotionPreference.Full, true, true)]
    [InlineData(false, MotionPreference.Full, true, false)]
    [InlineData(true, MotionPreference.Reduced, true, false)]
    [InlineData(true, MotionPreference.Full, false, false)]
    public void TheMeter_ShowsOnlyWhilePlaying_TurnedOn_AndNotUnderReducedMotion(
        bool playing, MotionPreference motion, bool turnedOn, bool shown) => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var vm = new MainViewModel();
        vm.Appearance.Motion = motion;
        vm.Appearance.ArtworkMeter = turnedOn;
        vm.Player.ShownTrack = new TrackInfo { VideoId = "a", Title = "A", Artists = [] };
        vm.Player.IsPlaying = playing;
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        var meter = window.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "BarVisualizer");
        var bars = meter.GetVisualDescendants().OfType<VisualizerControl>().Single();
        Assert.Equal(shown, meter.IsVisible);
        Assert.Equal(shown, bars.IsActive);
        window.Close();
    });
}
