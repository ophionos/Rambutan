using Avalonia.Threading;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>
/// Offline, the queue marks the rows that will be skipped because they are
/// neither downloaded nor cached, so the skip isn't a surprise.
/// </summary>
public class OfflineQueueTests
{
    private sealed class PlaysOffline(params string[] ids) : IOfflineAvailability
    {
        public HashSet<string> Ids { get; } = [.. ids];
        public bool IsAvailableOffline(string videoId) => Ids.Contains(videoId);
    }

    private static TrackInfo Track(string id) =>
        new() { VideoId = id, Title = id.ToUpperInvariant(), Artists = [new("Artist")] };

    private static (QueueModel Queue, QueueViewModel Vm, NetworkStatus Network) Create(
        PlaysOffline playsOffline, PlaybackCoordinator? coordinator = null, QueueModel? queue = null)
    {
        queue ??= new QueueModel();
        if (queue.Items.Count == 0)
            queue.SetQueue(new[] { "a", "b", "c" }.Select(Track));
        var network = new NetworkStatus(_ => Task.FromResult(Reachability.Unknown));
        return (queue, new QueueViewModel(queue, coordinator, network, playsOffline), network);
    }

    /// <summary>Waits for the availability pass, running the UI jobs it posts back.</summary>
    private static void Settle(QueueViewModel vm)
    {
        var update = vm.UpdateAvailabilityAsync();
        while (!update.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static bool[] Marks(QueueViewModel vm) => vm.Items.Select(i => i.NeedsConnection).ToArray();

    [Fact]
    public void Online_NoRowIsMarked() => Headless.Run(() =>
    {
        var (_, vm, _) = Create(new PlaysOffline());
        Settle(vm);
        Assert.Equal([false, false, false], Marks(vm));
    });

    [Fact]
    public void Offline_MarksWhatDoesntPlayOffline_AndSaysSo() => Headless.Run(() =>
    {
        var (_, vm, network) = Create(new PlaysOffline("b"));
        network.ReportOffline();
        Settle(vm);

        Assert.Equal([true, false, true], Marks(vm));
        Assert.Equal(
            Strings.Format("queue.row.needsConnection", ("row", "C, Artist")),
            vm.Items[2].AccessibleName);
        Assert.Equal("B, Artist", vm.Items[1].AccessibleName);
    });

    [Fact]
    public void BackOnline_ClearsTheMarks() => Headless.Run(() =>
    {
        var answer = Reachability.Unreachable;
        var queue = new QueueModel();
        queue.SetQueue(new[] { "a", "b" }.Select(Track));
        var network = new NetworkStatus(_ => Task.FromResult(answer), delay: (_, _) => new TaskCompletionSource().Task);
        var vm = new QueueViewModel(queue, null, network, new PlaysOffline());
        network.CheckAsync().GetAwaiter().GetResult();
        Settle(vm);
        Assert.Equal([true, true], Marks(vm));

        answer = Reachability.Reachable;
        network.CheckAsync().GetAwaiter().GetResult();
        Settle(vm);
        Assert.Equal([false, false], Marks(vm));
    });

    [Fact]
    public void ARowAddedOffline_IsMarkedToo() => Headless.Run(() =>
    {
        var (queue, vm, network) = Create(new PlaysOffline());
        network.ReportOffline();
        Settle(vm);

        queue.Enqueue(Track("d"));
        vm.Refresh();
        Settle(vm);

        Assert.True(vm.Items[^1].NeedsConnection);
    });

    [Fact]
    public void ThePlayingTrack_IsNeverMarked() => Headless.Run(() =>
    {
        var queue = new QueueModel();
        queue.SetQueue(new[] { "a", "b" }.Select(Track));
        var coordinator = FakePlayback.Coordinator(queue);
        coordinator.JumpToAsync(0).GetAwaiter().GetResult();
        var (_, vm, network) = Create(new PlaysOffline(), coordinator, queue);
        network.ReportOffline();
        Settle(vm);

        Assert.Equal([false, true], Marks(vm));
    });

    [Fact]
    public void ANetworkAlreadyOffline_MarksRowsFromTheStart() => Headless.Run(() =>
    {
        var queue = new QueueModel();
        queue.SetQueue(new[] { "a", "b" }.Select(Track));
        var network = new NetworkStatus(_ => Task.FromResult(Reachability.Unknown));
        network.ReportOffline();

        var vm = new QueueViewModel(queue, null, network, new PlaysOffline("a"));
        for (var i = 0; i < 1000 && !vm.Items[1].NeedsConnection; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        Assert.Equal([false, true], Marks(vm));
    });
}
