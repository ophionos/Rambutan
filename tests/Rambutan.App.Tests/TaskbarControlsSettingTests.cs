using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Settings;

namespace Rambutan.App.Tests;

/// <summary>Behavior's switch for the media controls on the Windows taskbar.</summary>
public class TaskbarControlsSettingTests
{
    private static (SettingsPageViewModel Page, AppSettings App, List<bool> Applied, Func<int> Saves) Create(
        AppSettings? app = null, bool composed = true)
    {
        app ??= new AppSettings();
        var applied = new List<bool>();
        var saves = 0;
        var page = new SettingsPageViewModel(
            new PlaybackSettings(), app, new AppearanceSettings(),
            new Rambutan.Integrations.UpdateService(null), new SessionViewModel(), () => saves++)
        {
            ApplyTaskbarControls = composed ? applied.Add : null,
        };
        return (page, app, applied, () => saves);
    }

    [Fact]
    public void TaskbarControls_DefaultOn()
    {
        Assert.True(new AppSettings().TaskbarControls);
        Assert.True(Create().Page.TaskbarControls);
    }

    [Fact]
    public void TaskbarControls_SeedsFromTheSettings() =>
        Assert.False(Create(new AppSettings { TaskbarControls = false }).Page.TaskbarControls);

    [Fact]
    public void TurningThemOff_AppliesAtOnceAndSaves()
    {
        var (page, app, applied, saves) = Create();

        page.TaskbarControls = false;

        Assert.False(app.TaskbarControls);
        Assert.Equal([false], applied);
        Assert.Equal(1, saves());
    }

    [Fact]
    public void TheRowShowsOnlyWhereThereAreTaskbarControls()
    {
        Assert.True(Create().Page.ShowsTaskbarControls);
        Assert.False(Create(composed: false).Page.ShowsTaskbarControls);
    }
}
