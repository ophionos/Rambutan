using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Settings;

namespace Rambutan.App.Tests;

/// <summary>The Appearance switch for the meter over the player bar's artwork.</summary>
public class ArtworkMeterSettingTests
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
    public void ArtworkMeter_DefaultsOn()
    {
        Assert.True(new AppSettings().ArtworkMeter);
        Assert.True(new AppearanceSettings().ArtworkMeter);
        Assert.True(Create().Page.ArtworkMeter);
    }

    [Fact]
    public void TurningTheMeterOff_ReachesTheShellsSettingAndSaves()
    {
        var (page, appearance, saves) = Create();

        page.ArtworkMeter = false;

        Assert.False(appearance.ArtworkMeter);
        Assert.Equal(1, saves());
    }

    [Fact]
    public void ArtworkMeter_SeedsFromTheLiveSetting() =>
        Assert.False(Create(new AppearanceSettings { ArtworkMeter = false }).Page.ArtworkMeter);
}
