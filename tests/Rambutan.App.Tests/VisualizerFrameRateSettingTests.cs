using Avalonia.Controls;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Models;
using Rambutan.Core.Settings;

namespace Rambutan.App.Tests;

/// <summary>The Appearance choice that caps how often the meter and the Now
/// Playing visualizer redraw.</summary>
public class VisualizerFrameRateSettingTests
{
    private static (SettingsPageViewModel Page, AppearanceSettings Appearance, Func<int> Saves) Create(
        AppearanceSettings? appearance = null)
    {
        appearance ??= new AppearanceSettings();
        var saves = 0;
        var page = new SettingsPageViewModel(
            new PlaybackSettings(), new AppSettings(), appearance,
            new Rambutan.Integrations.UpdateService(null), new SessionViewModel(), () => saves++);
        return (page, appearance, () => saves);
    }

    [Fact]
    public void TheRate_DefaultsTo30()
    {
        Assert.Equal(30, new AppSettings().VisualizerFrameRate);
        Assert.Equal(30, new AppearanceSettings().VisualizerFrameRate);
        Assert.Equal(0, Create().Page.VisualizerFrameRateIndex);
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 0)]
    [InlineData(0, 30)]
    public void AChoice_ReachesTheShellsSettingAndSaves(int index, int rate)
    {
        var (page, appearance, saves) = Create(new AppearanceSettings { VisualizerFrameRate = 60 });
        page.VisualizerFrameRateIndex = 2;
        page.VisualizerFrameRateIndex = index;

        Assert.Equal(rate, appearance.VisualizerFrameRate);
        Assert.True(saves() >= 1);
    }

    [Fact]
    public void TheChoice_SeedsFromTheLiveSetting() =>
        Assert.Equal(2, Create(new AppearanceSettings { VisualizerFrameRate = 0 }).Page.VisualizerFrameRateIndex);

    [Theory]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(0, 0)]
    [InlineData(144, 30)]
    [InlineData(-5, 30)]
    public void ASavedRate_ThatIsNotAChoice_ReadsAsTheDefault(int saved, int read) =>
        Assert.Equal(read, AppearanceSettings.ParseFrameRate(saved));

    [Fact]
    public void TheMeterAndNowPlaying_FollowTheSetting() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var vm = new MainViewModel();
        vm.Appearance.Motion = MotionPreference.Full;
        vm.Player.ShownTrack = new TrackInfo { VideoId = "a", Title = "A", Artists = [] };
        vm.Player.NowPlaying = vm.Player.ShownTrack;
        vm.Player.IsPlaying = true;
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.NowPlaying.IsOpen = true;
        window.UpdateLayout();

        var meter = window.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "BarVisualizer")
            .GetVisualDescendants().OfType<VisualizerControl>().Single();
        var band = window.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "VisualizerBand")
            .GetVisualDescendants().OfType<VisualizerControl>().Single();
        Assert.Equal(30, meter.MaxFrameRate);
        Assert.Equal(30, band.MaxFrameRate);

        vm.Appearance.VisualizerFrameRate = 0;
        Assert.Equal(0, meter.MaxFrameRate);
        Assert.Equal(0, band.MaxFrameRate);
        window.Close();
    });
}
