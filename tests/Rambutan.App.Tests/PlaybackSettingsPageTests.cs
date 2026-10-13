using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Settings;

namespace Rambutan.App.Tests;

public class PlaybackSettingsPageTests
{
    private static (SettingsPageViewModel Page, PlaybackSettings Playback, AppSettings App, Func<int> Saves) Create(
        PlaybackSettings? playback = null, AppSettings? app = null)
    {
        playback ??= new PlaybackSettings();
        app ??= new AppSettings();
        var saves = 0;
        var page = new SettingsPageViewModel(
            playback, app, new AppearanceSettings(), new Rambutan.Integrations.UpdateService(null), new SessionViewModel(), () => saves++);
        return (page, playback, app, () => saves);
    }

    [Fact]
    public void Autoplay_DefaultsOn()
    {
        Assert.True(new AppSettings().Autoplay);
        Assert.True(new PlaybackSettings().Autoplay);
        Assert.True(Create().Page.Autoplay);
    }

    [Fact]
    public void TurningAutoplayOff_ReachesTheCoordinatorsSettingAndSaves()
    {
        var (page, playback, _, saves) = Create();

        page.Autoplay = false;

        Assert.False(playback.Autoplay);
        Assert.Equal(1, saves());
    }

    [Fact]
    public void Autoplay_SeedsFromTheLiveSetting() =>
        Assert.False(Create(new PlaybackSettings { Autoplay = false }).Page.Autoplay);

    [Fact]
    public void AudioQuality_DefaultsToHigh()
    {
        Assert.Equal("high", new AppSettings().AudioQuality);
        Assert.Equal(AudioQuality.High, new PlaybackSettings().AudioQuality);
        Assert.Equal(0, Create().Page.AudioQualityIndex);
    }

    [Fact]
    public void ChoosingDataSaver_ReachesTheResolversSettingAndSaves()
    {
        var (page, playback, _, saves) = Create();

        page.AudioQualityIndex = 1;

        Assert.Equal(AudioQuality.DataSaver, playback.AudioQuality);
        Assert.Equal(1, saves());
    }

    [Fact]
    public void AudioQuality_IgnoresAnIndexWithNoTier()
    {
        var (page, playback, _, saves) = Create();

        page.AudioQualityIndex = -1;

        Assert.Equal(AudioQuality.High, playback.AudioQuality);
        Assert.Equal(0, saves());
    }

    [Theory]
    [InlineData(512, "512 MB")]
    [InlineData(128, "128 MB")]
    [InlineData(0, "Off")]
    public void CacheSize_SaysOff_AtZero(int megabytes, string expected) =>
        Assert.Equal(expected, Create(app: new AppSettings { AudioCacheSizeMb = megabytes }).Page.AudioCacheSizeText);

    [Fact]
    public void MovingTheCacheSlider_RewordsTheValue()
    {
        var (page, _, app, _) = Create();
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.AudioCacheSizeMb = 0;

        Assert.Equal(0, app.AudioCacheSizeMb);
        Assert.Contains(nameof(SettingsPageViewModel.AudioCacheSizeText), changed);
        Assert.Equal("Off", page.AudioCacheSizeText);
    }

    [Fact]
    public void ABuildThatCantUpdateItself_SaysSoAtRest_AndOffersNoCheck()
    {
        // The note used to appear only after pressing a button that could do nothing.
        var page = Create().Page;

        Assert.Equal("Updates apply to installed builds only.", page.UpdateStatus);
        Assert.False(page.CheckForUpdatesCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CloseToTray_SaysSoWhenTheDesktopShowsNoTray(bool trayShows, bool noteShown)
    {
        var (page, _, _, _) = Create();
        Assert.False(page.NoTray);

        await page.CheckTrayAsync(() => Task.FromResult(trayShows));

        Assert.Equal(noteShown, page.NoTray);
    }
}
