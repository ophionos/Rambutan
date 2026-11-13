using Rambutan.App.Services;
using Rambutan.Integrations;
using Velopack;

namespace Rambutan.App.Tests;

/// <summary>
/// Restart now with other instances running: the updater closes them without
/// saving, so it asks first, and only Yes restarts.
/// </summary>
public sealed class UpdateRestartTests
{
    private readonly List<string> _asked = [];
    private int _restarts;

    private UpdateRestart Restart(bool othersRunning, bool answer) => new(
        UpdateService.Over(new Updater(othersRunning)),
        (message, accept) =>
        {
            _asked.Add(message);
            return Task.FromResult(answer);
        },
        () => _restarts++);

    [Fact]
    public async Task Alone_RestartsWithoutAsking()
    {
        await Restart(othersRunning: false, answer: false).RunAsync();

        Assert.Empty(_asked);
        Assert.Equal(1, _restarts);
    }

    [Fact]
    public async Task WithOthers_AsksAndRestartsOnYes()
    {
        await Restart(othersRunning: true, answer: true).RunAsync();

        Assert.Single(_asked);
        Assert.Equal(1, _restarts);
    }

    [Fact]
    public async Task WithOthers_NoLeavesEverythingRunning()
    {
        await Restart(othersRunning: true, answer: false).RunAsync();

        Assert.Single(_asked);
        Assert.Equal(0, _restarts);
    }

    private sealed class Updater(bool othersRunning) : IUpdater
    {
        public bool IsInstalled => true;
        public string? CurrentVersion => "1.0.0";
        public Task<UpdateInfo?> CheckForUpdatesAsync() => Task.FromResult<UpdateInfo?>(null);
        public Task DownloadUpdatesAsync(UpdateInfo info, CancellationToken ct) => Task.CompletedTask;
        public void WaitExitThenApply(VelopackAsset target) { }
        public void ApplyAndRestart(VelopackAsset target, string[]? args) { }
        public bool OtherInstancesRunning() => othersRunning;
    }
}
