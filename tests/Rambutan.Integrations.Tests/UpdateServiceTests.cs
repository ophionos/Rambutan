using Velopack;

namespace Rambutan.Integrations.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData(UpdateService.ReleasesFeed, true)]
    [InlineData("https://GitHub.com/someone/fork", true)]
    [InlineData("http://github.com/someone/fork", false)]
    [InlineData("https://example.com/releases", false)]
    [InlineData(@"C:\releases", false)]
    [InlineData("/srv/releases", false)]
    public void IsGitHubRepository_OnlyForHttpsGitHubUrls(string feed, bool expected) =>
        Assert.Equal(expected, UpdateService.IsGitHubRepository(feed));

    [Fact]
    public async Task CheckAndStage_DownloadsButSchedulesNoInstall()
    {
        var updater = new FakeUpdater { Available = Release("1.0.1") };
        var updates = new UpdateService(updater);

        Assert.Equal("1.0.1", await updates.CheckAndStageAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, updater.Downloads);
        Assert.Empty(updater.Applies);
    }

    [Fact]
    public async Task CheckAndStage_ChecksOnlyUntilSomethingIsStaged()
    {
        var updater = new FakeUpdater { Available = Release("1.0.1") };
        var updates = new UpdateService(updater);

        await updates.CheckAndStageAsync(TestContext.Current.CancellationToken);
        await updates.CheckAndStageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, updater.Checks);
        Assert.Equal(1, updater.Downloads);
    }

    [Fact]
    public async Task ApplyOnExit_SchedulesTheStagedReleaseOnce()
    {
        var updater = new FakeUpdater { Available = Release("1.0.1") };
        var updates = new UpdateService(updater);
        await updates.CheckAndStageAsync(TestContext.Current.CancellationToken);

        updates.ApplyOnExit();
        updates.ApplyOnExit();

        Assert.Equal(["wait-exit 1.0.1"], updater.Applies);
    }

    [Fact]
    public void ApplyOnExit_WithNothingStaged_DoesNothing()
    {
        var updater = new FakeUpdater();
        new UpdateService(updater).ApplyOnExit();

        Assert.Empty(updater.Applies);
    }

    [Fact]
    public async Task RestartNow_AppliesOnce_AndExitAddsNoSecondApply()
    {
        var updater = new FakeUpdater { Available = Release("1.0.1") };
        var updates = new UpdateService(updater);
        await updates.CheckAndStageAsync(TestContext.Current.CancellationToken);

        updates.RestartNow(["--profile", "abc"]);
        updates.ApplyOnExit();

        Assert.Equal(["restart 1.0.1 --profile abc"], updater.Applies);
    }

    [Fact]
    public async Task NotInstalled_NeverChecks()
    {
        var updater = new FakeUpdater { IsInstalled = false, Available = Release("1.0.1") };
        var updates = new UpdateService(updater);

        Assert.Null(await updates.CheckAndStageAsync(TestContext.Current.CancellationToken));
        updates.ApplyOnExit();

        Assert.Equal(0, updater.Checks);
        Assert.Empty(updater.Applies);
    }

    private static UpdateInfo Release(string version) =>
        new(new VelopackAsset { Version = SemanticVersion.Parse(version) }, false);

    private sealed class FakeUpdater : IUpdater
    {
        public bool IsInstalled { get; init; } = true;
        public string? CurrentVersion => "1.0.0";
        public UpdateInfo? Available { get; init; }
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public List<string> Applies { get; } = [];

        public Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            Checks++;
            return Task.FromResult(Available);
        }

        public Task DownloadUpdatesAsync(UpdateInfo info, CancellationToken ct)
        {
            Downloads++;
            return Task.CompletedTask;
        }

        public void WaitExitThenApply(VelopackAsset target) =>
            Applies.Add($"wait-exit {target.Version}");

        public void ApplyAndRestart(VelopackAsset target, string[]? args) =>
            Applies.Add(string.Join(' ', ["restart", target.Version.ToString(), .. args ?? []]));
    }
}
