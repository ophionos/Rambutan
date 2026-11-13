using Avalonia.Threading;
using Rambutan.App.ViewModels;
using Rambutan.Integrations;
using Velopack;

namespace Rambutan.App.Tests;

/// <summary>
/// The status strip's "update ready" slot: it shows once a download is staged,
/// Dismiss hides it for the run, and Restart now hands over to the app's
/// shutdown path rather than installing itself.
/// </summary>
public sealed class UpdateNoticeTests
{
    private int _restarts;

    [Fact]
    public void HiddenUntilAnUpdateIsStaged_ThenSaysWhichOne() => Headless.Run(() =>
    {
        var updates = UpdateService.Over(new StagingUpdater());
        var main = new MainViewModel { UpdateNotice = new UpdateNoticeViewModel(updates, () => _restarts++) };
        var notice = main.UpdateNotice!;
        Assert.False(notice.IsVisible);
        Assert.False(main.HasStatus);

        updates.CheckAndStageAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        Assert.True(notice.IsVisible);
        Assert.True(main.HasStatus);
        Assert.Contains("1.0.1", notice.Text);
    });

    [Fact]
    public void Dismiss_HidesItForTheRun() => Headless.Run(() =>
    {
        var updates = UpdateService.Over(new StagingUpdater());
        updates.CheckAndStageAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        var main = new MainViewModel { UpdateNotice = new UpdateNoticeViewModel(updates, () => _restarts++) };

        main.UpdateNotice!.DismissCommand.Execute(null);

        Assert.False(main.UpdateNotice.IsVisible);
        Assert.False(main.HasStatus);
        Assert.Equal("1.0.1", updates.StagedVersion);
    });

    [Fact]
    public void RestartNow_UsesTheAppsRestart() => Headless.Run(() =>
    {
        var updater = new StagingUpdater();
        var updates = UpdateService.Over(updater);
        updates.CheckAndStageAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        var notice = new UpdateNoticeViewModel(updates, () => _restarts++);

        notice.RestartNowCommand.Execute(null);

        Assert.Equal(1, _restarts);
        Assert.Equal(0, updater.Applies);
    });

    private sealed class StagingUpdater : IUpdater
    {
        public int Applies { get; private set; }
        public bool IsInstalled => true;
        public string? CurrentVersion => "1.0.0";

        public Task<UpdateInfo?> CheckForUpdatesAsync() => Task.FromResult<UpdateInfo?>(
            new UpdateInfo(new VelopackAsset { Version = SemanticVersion.Parse("1.0.1") }, false));

        public Task DownloadUpdatesAsync(UpdateInfo info, CancellationToken ct) => Task.CompletedTask;
        public void WaitExitThenApply(VelopackAsset target) => Applies++;
        public void ApplyAndRestart(VelopackAsset target, string[]? args) => Applies++;
        public bool OtherInstancesRunning() => false;
    }
}
