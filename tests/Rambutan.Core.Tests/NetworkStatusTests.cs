using Rambutan.Core.Playback;

namespace Rambutan.Core.Tests;

/// <summary>The app's standing idea of whether YouTube Music can be reached:
/// set by checks and by failures, and expired by re-checks while offline.</summary>
public class NetworkStatusTests
{
    private readonly Queue<Reachability> _answers = new();
    private readonly List<bool> _changes = [];
    private int _checks;
    private TaskCompletionSource _wait = new();
    private readonly List<TimeSpan> _delays = [];

    private NetworkStatus Create()
    {
        var status = new NetworkStatus(
            _ =>
            {
                _checks++;
                return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : Reachability.Unknown);
            },
            TimeSpan.FromSeconds(30),
            (wait, _) =>
            {
                _delays.Add(wait);
                return _wait.Task;
            });
        status.Changed += _changes.Add;
        return status;
    }

    /// <summary>Lets the pending re-check delay end and gives the loop a moment to run.</summary>
    private async Task ElapseAsync()
    {
        var wait = _wait;
        _wait = new TaskCompletionSource();
        wait.SetResult();
        await Task.Yield();
    }

    [Fact]
    public async Task AFailedCheck_GoesOffline_AndAnAnsweredOne_ComesBack()
    {
        var status = Create();
        _answers.Enqueue(Reachability.Unreachable);
        await status.CheckAsync();
        Assert.True(status.IsOffline);

        _answers.Enqueue(Reachability.Reachable);
        await status.CheckAsync();
        Assert.False(status.IsOffline);
        Assert.Equal([true, false], _changes);
    }

    [Fact]
    public async Task AnUnknownAnswer_ChangesNothing()
    {
        var status = Create();
        status.ReportOffline();
        _answers.Enqueue(Reachability.Unknown);
        await status.CheckAsync();

        Assert.True(status.IsOffline);
        Assert.Equal([true], _changes);
    }

    [Fact]
    public async Task AThrowingCheck_CountsAsUnknown()
    {
        var status = new NetworkStatus(_ => throw new InvalidOperationException("boom"));
        await status.CheckAsync();
        Assert.False(status.IsOffline);
    }

    [Fact]
    public async Task Offline_ChecksAgain_UntilTheNetworkAnswers()
    {
        var status = Create();
        status.ReportOffline();
        Assert.Equal(0, _checks);
        Assert.Equal([TimeSpan.FromSeconds(30)], _delays);

        _answers.Enqueue(Reachability.Unreachable);
        await ElapseAsync();
        Assert.Equal(1, _checks);
        Assert.True(status.IsOffline);
        Assert.Equal(2, _delays.Count);

        _answers.Enqueue(Reachability.Reachable);
        await ElapseAsync();
        Assert.Equal(2, _checks);
        Assert.False(status.IsOffline);

        // Online, it doesn't check on its own.
        await ElapseAsync();
        Assert.Equal(2, _checks);
        Assert.Equal(2, _delays.Count);
    }

    [Fact]
    public void RepeatedReports_RunOneRecheckLoop()
    {
        var status = Create();
        status.ReportOffline();
        status.ReportOffline();
        Assert.Single(_delays);
        Assert.Equal([true], _changes);
    }
}
