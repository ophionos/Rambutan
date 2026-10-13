using System.Collections.Concurrent;
using Rambutan.Core.Playback;

namespace Rambutan.Core.Tests;

/// <summary>Hover warm-up scheduling with a hand-driven clock and a fake
/// warmer: no timers, no I/O.</summary>
public class HoverWarmupTests
{
    /// <summary>Each dwell waits for the test to elapse it (or for cancellation).</summary>
    private sealed class ManualDelays
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _pending = new();

        public int Started { get; private set; }

        public Task Delay(TimeSpan _, CancellationToken ct)
        {
            Started++;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));
            _pending.Enqueue(tcs);
            return tcs.Task;
        }

        /// <summary>Elapses every dwell waiting now.</summary>
        public void ElapseAll()
        {
            while (_pending.TryDequeue(out var tcs))
                tcs.TrySetResult();
        }
    }

    private sealed class FakeWarmer : IStreamWarmer
    {
        private readonly object _sync = new();
        private int _running;

        public HashSet<string> Warm { get; } = [];
        public List<string> Started { get; } = [];
        public ConcurrentDictionary<string, TaskCompletionSource> Runs { get; } = new();
        public List<string> Cancelled { get; } = [];

        public bool IsWarm(string videoId)
        {
            lock (_sync) return Warm.Contains(videoId);
        }

        public async Task WarmAsync(string videoId, CancellationToken ct)
        {
            var run = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                Started.Add(videoId);
                _running++;
            }
            Runs[videoId] = run;
            try
            {
                await run.Task.WaitAsync(ct);
                lock (_sync) Warm.Add(videoId);
            }
            catch (OperationCanceledException)
            {
                lock (_sync) Cancelled.Add(videoId);
                throw;
            }
            finally
            {
                lock (_sync) _running--;
            }
        }

        public void Finish(string videoId) => Runs[videoId].TrySetResult();
    }

    private static (HoverWarmup Warmup, FakeWarmer Warmer, ManualDelays Clock) Create(int maxConcurrent = 2)
    {
        var warmer = new FakeWarmer();
        var clock = new ManualDelays();
        return (new HoverWarmup(warmer, maxConcurrent: maxConcurrent, delay: clock.Delay), warmer, clock);
    }

    /// <summary>Lets continuations scheduled on the pool run.</summary>
    private static async Task Settle()
    {
        for (var i = 0; i < 5; i++)
            await Task.Delay(10);
    }

    [Fact]
    public void DefaultDwell_IsAboutFourHundredMilliseconds() =>
        Assert.Equal(TimeSpan.FromMilliseconds(400), HoverWarmup.DefaultDwell);

    [Fact]
    public async Task Resting_ForTheDwell_Resolves()
    {
        var (warmup, warmer, clock) = Create();

        warmup.Enter("a");
        await Settle();
        Assert.Empty(warmer.Started);

        clock.ElapseAll();
        await Settle();
        Assert.Equal(["a"], warmer.Started);

        warmer.Finish("a");
        await warmup.Last;
        Assert.Contains("a", warmer.Warm);
    }

    [Fact]
    public async Task Leaving_BeforeTheDwell_ResolvesNothing()
    {
        var (warmup, warmer, clock) = Create();

        warmup.Enter("a");
        warmup.Leave("a");
        clock.ElapseAll();
        await warmup.Last;

        Assert.Empty(warmer.Started);
    }

    [Fact]
    public async Task Leaving_CancelsAResolutionInFlight()
    {
        var (warmup, warmer, clock) = Create();
        warmup.Enter("a");
        clock.ElapseAll();
        await Settle();

        warmup.Leave("a");
        await warmup.Last;

        Assert.Equal(["a"], warmer.Cancelled);
    }

    [Fact]
    public async Task EnteringAnotherRow_ReplacesTheFirst()
    {
        var (warmup, warmer, clock) = Create();

        warmup.Enter("a");
        var first = warmup.Last;
        warmup.Enter("b");
        clock.ElapseAll();
        await first;
        await Settle();

        Assert.Equal(["b"], warmer.Started);
    }

    [Fact]
    public async Task EnteringAnotherRow_CancelsTheFirstsResolution()
    {
        var (warmup, warmer, clock) = Create();
        warmup.Enter("a");
        clock.ElapseAll();
        await Settle();

        warmup.Enter("b");
        await Settle();

        Assert.Equal(["a"], warmer.Cancelled);
    }

    [Fact]
    public async Task AStaleLeave_ForARowAlreadyReplaced_ChangesNothing()
    {
        var (warmup, warmer, clock) = Create();

        warmup.Enter("a");
        warmup.Enter("b");
        warmup.Leave("a");
        clock.ElapseAll();
        await Settle();

        Assert.Equal(["b"], warmer.Started);
    }

    [Fact]
    public async Task ReEnteringTheSameRow_DoesntRestartTheDwell()
    {
        var (warmup, _, clock) = Create();

        warmup.Enter("a");
        warmup.Enter("a");
        await Settle();

        Assert.Equal(1, clock.Started);
    }

    [Fact]
    public async Task AWarmTrack_IsSkipped()
    {
        var (warmup, warmer, clock) = Create();
        warmer.Warm.Add("a");

        warmup.Enter("a");
        clock.ElapseAll();
        await warmup.Last;

        Assert.Empty(warmer.Started);
    }

    [Fact]
    public async Task ASlotStillHeld_DelaysTheNextResolutionUntilItsFree()
    {
        var warmer = new BlockingWarmer();
        var clock = new ManualDelays();
        var warmup = new HoverWarmup(warmer, maxConcurrent: 1, delay: clock.Delay);

        warmup.Enter("a");
        clock.ElapseAll();
        await Settle();
        warmup.Enter("b");          // cancels "a", which ignores it for now
        clock.ElapseAll();
        await Settle();
        Assert.Equal(["a"], warmer.Started);

        warmer.Release("a");        // "a" finally stops
        await Settle();
        Assert.Equal(["a", "b"], warmer.Started);
    }

    [Fact]
    public async Task AFailedResolution_IsSwallowed()
    {
        var warmer = new ThrowingWarmer();
        var clock = new ManualDelays();
        var warmup = new HoverWarmup(warmer, delay: clock.Delay);

        warmup.Enter("a");
        clock.ElapseAll();

        await warmup.Last; // completes, doesn't throw
        Assert.True(warmer.Called);
    }

    [Fact]
    public async Task Dispose_CancelsTheHover()
    {
        var (warmup, warmer, clock) = Create();
        warmup.Enter("a");

        warmup.Dispose();
        clock.ElapseAll();
        await warmup.Last;

        Assert.Empty(warmer.Started);
    }

    /// <summary>A warmer whose runs ignore cancellation until released, like a
    /// yt-dlp process that takes a moment to die.</summary>
    private sealed class BlockingWarmer : IStreamWarmer
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _runs = new();
        public List<string> Started { get; } = [];

        public bool IsWarm(string videoId) => false;

        public Task WarmAsync(string videoId, CancellationToken ct)
        {
            lock (Started) Started.Add(videoId);
            return _runs.GetOrAdd(videoId, _ => new TaskCompletionSource()).Task;
        }

        public void Release(string videoId) => _runs[videoId].TrySetResult();
    }

    private sealed class ThrowingWarmer : IStreamWarmer
    {
        public bool Called { get; private set; }

        public bool IsWarm(string videoId) => false;

        public Task WarmAsync(string videoId, CancellationToken ct)
        {
            Called = true;
            throw new InvalidOperationException("yt-dlp said no");
        }
    }
}
