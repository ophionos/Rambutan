using Rambutan.Core.Downloads;
using Rambutan.Core.Models;

namespace Rambutan.Core.Tests;

public class DownloadQueueTests
{
    private static TrackInfo Track(string id) => new() { VideoId = id, Title = id, Artists = [] };

    [Fact]
    public async Task RunsAtMostMaxParallelJobs_AndStartsTheNextWhenOneFinishes()
    {
        var store = new FakeDownloadStore();
        var queue = new DownloadQueue(store, maxParallel: 2);

        queue.Enqueue([Track("a"), Track("b"), Track("c")]);
        await store.WaitStartedAsync("a");
        await store.WaitStartedAsync("b");

        // The two parallel jobs start on the thread pool, so their order isn't fixed.
        Assert.Equal(["a", "b"], store.Started.Order());
        Assert.Equal(DownloadJobState.Queued, queue.Jobs.Single(j => j.Track.VideoId == "c").State);

        store.Finish("a");
        await store.WaitStartedAsync("c");
        await WaitUntilAsync(() => queue.IsDownloaded("a") && !queue.IsQueued("a"));
        Assert.Equal("c", store.Started[^1]);
        Assert.Equal(3, store.Started.Count);
    }

    [Fact]
    public async Task Enqueue_SkipsDownloadedAndAlreadyQueuedTracks()
    {
        var store = new FakeDownloadStore();
        store.Done.Add("a");
        var queue = new DownloadQueue(store);

        Assert.Equal(1, queue.Enqueue([Track("a"), Track("b")]));
        Assert.Equal(0, queue.Enqueue([Track("b")]));
        await store.WaitStartedAsync("b");
        Assert.Equal(["b"], queue.Jobs.Select(j => j.Track.VideoId));
    }

    [Fact]
    public async Task Failure_KeepsTheJobWithItsReason_AndRetryRunsItAgain()
    {
        var store = new FakeDownloadStore();
        var queue = new DownloadQueue(store);

        queue.Enqueue([Track("a")]);
        await store.WaitStartedAsync("a");
        store.Fail("a", "403");
        await WaitUntilAsync(() => queue.Jobs.Single().State is DownloadJobState.Failed);
        Assert.Equal("403", queue.Jobs.Single().Error);

        queue.Retry("a");
        await WaitUntilAsync(() => store.Started.Count(id => id == "a") == 2);
        Assert.Equal(DownloadJobState.Downloading, queue.Jobs.Single().State);
        Assert.Null(queue.Jobs.Single().Error);
    }

    [Fact]
    public async Task Cancel_StopsTheRunningDownloadAndStartsTheNext()
    {
        var store = new FakeDownloadStore();
        var queue = new DownloadQueue(store, maxParallel: 1);

        queue.Enqueue([Track("a"), Track("b")]);
        await store.WaitStartedAsync("a");
        queue.Cancel("a");

        await store.WaitStartedAsync("b");
        await WaitUntilAsync(() => store.Cancelled.Contains("a"));
        Assert.False(queue.IsQueued("a"));
        Assert.False(queue.IsDownloaded("a"));
    }

    [Fact]
    public async Task PendingJobsAreSavedAndRestored()
    {
        var store = new FakeDownloadStore();
        var queue = new DownloadQueue(store, maxParallel: 1);
        queue.Enqueue([Track("a"), Track("b")]);
        await store.WaitStartedAsync("a");

        Assert.Equal(["a", "b"], store.Pending.Select(p => p.Track.VideoId));

        store.Finish("a");
        await WaitUntilAsync(() => store.Pending.Select(p => p.Track.VideoId).SequenceEqual(["b"]));

        var restarted = new DownloadQueue(store, maxParallel: 1);
        restarted.Restore();
        Assert.Equal(["b"], restarted.Jobs.Select(j => j.Track.VideoId));
        Assert.Equal(queue.Jobs.Single().Order, restarted.Jobs.Single().Order);
    }

    [Fact]
    public async Task OneRequest_SharesItsTime_AndNumbersItsTracksInTheGivenOrder()
    {
        var store = new FakeDownloadStore();
        store.Done.Add("b");
        var queue = new DownloadQueue(store, maxParallel: 3);

        queue.Enqueue([Track("a"), Track("b"), Track("c")]);
        await store.WaitStartedAsync("a");
        await store.WaitStartedAsync("c");

        Assert.Equal(store.Orders["a"].RequestedAt, store.Orders["c"].RequestedAt);
        // Positions are places in the request, so a skipped track leaves a gap, not a shift.
        Assert.Equal(0, store.Orders["a"].Position);
        Assert.Equal(2, store.Orders["c"].Position);

        store.Fail("c", "403");
        await WaitUntilAsync(() => queue.Jobs.Single(j => j.Track.VideoId == "c").State is DownloadJobState.Failed);
        queue.Retry("c");
        await WaitUntilAsync(() => store.Started.Count(id => id == "c") == 2);
        Assert.Equal(2, store.Orders["c"].Position); // a retry keeps its place
    }

    [Fact]
    public async Task DeletingMany_StopsTheirJobs_AndDeletesInOneStoreCall()
    {
        var store = new FakeDownloadStore();
        store.Done.UnionWith(["a", "b"]);
        var queue = new DownloadQueue(store, maxParallel: 1);
        queue.Enqueue([Track("c"), Track("d")]);
        await store.WaitStartedAsync("c");
        List<string> changed = [];
        queue.DownloadsChanged += changed.Add;

        queue.Delete(["a", "b", "c", "d"]);

        Assert.Equal(["a", "b", "c", "d"], Assert.Single(store.Deletes).Order());
        Assert.Empty(queue.Jobs);
        Assert.False(queue.IsDownloaded("a"));
        Assert.Equal(["a", "b", "c", "d"], changed.Order());
    }

    [Fact]
    public void Delete_RemovesTheDownloadAndSaysSo()
    {
        var store = new FakeDownloadStore();
        store.Done.Add("a");
        var queue = new DownloadQueue(store);
        string? changed = null;
        queue.DownloadsChanged += id => changed = id;

        queue.Delete("a");

        Assert.False(queue.IsDownloaded("a"));
        Assert.Equal("a", changed);
    }

    [Fact]
    public async Task ProgressIsReportedOnTheJob()
    {
        var store = new FakeDownloadStore();
        var queue = new DownloadQueue(store);
        queue.Enqueue([Track("a")]);
        await store.WaitStartedAsync("a");

        store.Report("a", new DownloadProgress(50, 200));

        Assert.Equal(0.25, queue.Jobs.Single().Progress.Fraction);
    }

    [Fact]
    public void Fraction_IsNullUntilTheTotalIsKnown()
    {
        Assert.Null(new DownloadProgress(10, null).Fraction);
        Assert.Null(new DownloadProgress(10, 0).Fraction);
        Assert.Equal(1, new DownloadProgress(300, 200).Fraction);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not met");
            await Task.Delay(5);
        }
    }

    private sealed class FakeDownloadStore : IDownloadStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, Running> _running = [];
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly List<string> _startedOrder = [];

        public readonly HashSet<string> Done = [];
        public readonly List<string> Cancelled = [];
        public IReadOnlyList<PendingDownload> Pending { get; private set; } = [];

        /// <summary>The order each started download was handed, by videoId.</summary>
        public readonly Dictionary<string, DownloadOrder> Orders = [];

        public IReadOnlyList<string> Started
        {
            get { lock (_lock) return _startedOrder.ToArray(); }
        }

        private sealed record Running(TaskCompletionSource Completion, IProgress<DownloadProgress> Progress);

        public IReadOnlyList<DownloadedTrack> List() =>
            Done.Select(id => new DownloadedTrack(Track(id), 1, DateTimeOffset.UnixEpoch)).ToArray();

        public bool Contains(string videoId)
        {
            lock (_lock) return Done.Contains(videoId);
        }

        public async Task DownloadAsync(
            TrackInfo track, DownloadOrder order, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            var running = new Running(
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), progress);
            lock (_lock)
            {
                Orders[track.VideoId] = order;
                _running[track.VideoId] = running;
                _startedOrder.Add(track.VideoId);
                StartedSignal(track.VideoId).TrySetResult();
            }

            try
            {
                await running.Completion.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                lock (_lock) Cancelled.Add(track.VideoId);
                throw;
            }

            lock (_lock) Done.Add(track.VideoId);
        }

        private TaskCompletionSource StartedSignal(string videoId)
        {
            if (!_started.TryGetValue(videoId, out var tcs))
                _started[videoId] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return tcs;
        }

        public Task WaitStartedAsync(string videoId)
        {
            lock (_lock) return StartedSignal(videoId).Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private Running Get(string videoId)
        {
            lock (_lock) return _running[videoId];
        }

        public void Finish(string videoId) => Get(videoId).Completion.SetResult();

        public void Fail(string videoId, string reason) =>
            Get(videoId).Completion.SetException(new InvalidOperationException(reason));

        public void Report(string videoId, DownloadProgress progress) => Get(videoId).Progress.Report(progress);

        public List<IReadOnlyCollection<string>> Deletes { get; } = [];

        public void Delete(IReadOnlyCollection<string> videoIds)
        {
            lock (_lock)
            {
                Done.ExceptWith(videoIds);
                Deletes.Add(videoIds.ToList());
            }
        }

        public IReadOnlyList<PendingDownload> LoadPending() => Pending;

        public void SavePending(IReadOnlyList<PendingDownload> pending) => Pending = pending.ToArray();
    }
}
