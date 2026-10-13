using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.Core.Tests;

internal sealed class FakeStreamSource : IStreamSource, IOfflineAvailability
{
    private readonly object _lock = new();
    private readonly List<(string VideoId, bool Bypass)> _calls = [];
    private readonly Dictionary<string, TaskCompletionSource> _blocks = [];

    public readonly HashSet<string> FailOnFirstTry = [];
    public readonly HashSet<string> FailAlways = [];
    public readonly Dictionary<string, double> LoudnessDb = [];

    /// <summary>The network is down: every track outside <see cref="AvailableOffline"/>
    /// fails with <see cref="OfflineException"/>.</summary>
    public bool Offline { get; set; }

    public readonly HashSet<string> AvailableOffline = [];

    public bool IsAvailableOffline(string videoId) => AvailableOffline.Contains(videoId);

    public IReadOnlyList<(string VideoId, bool Bypass)> Calls
    {
        get { lock (_lock) return _calls.ToArray(); }
    }

    /// <summary>Makes resolves of <paramref name="videoId"/> wait until the returned source completes.</summary>
    public TaskCompletionSource Block(string videoId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _blocks[videoId] = tcs;
        return tcs;
    }

    public async Task<ResolvedMedia> ResolveAsync(
        string videoId, bool bypassCache = false, CancellationToken ct = default)
    {
        TaskCompletionSource? blocked;
        lock (_lock)
        {
            _calls.Add((videoId, bypassCache));
            _blocks.TryGetValue(videoId, out blocked);
        }

        if (blocked is not null)
            await blocked.Task.WaitAsync(ct);

        if (Offline && !AvailableOffline.Contains(videoId))
            throw new OfflineException("network unreachable");
        if (FailAlways.Contains(videoId))
            throw new InvalidOperationException($"resolve failed for {videoId}");
        if (!bypassCache && FailOnFirstTry.Contains(videoId))
            throw new InvalidOperationException($"transient failure for {videoId}");

        return new ResolvedMedia(
            $"https://cdn.test/{videoId}", "opus",
            LoudnessDb.TryGetValue(videoId, out var db) ? db : null);
    }
}

internal sealed class FakeAudioPlayer : IAudioPlayer
{
    private readonly object _lock = new();
    private readonly List<string> _log = [];

    public AudioTrackRequest? Current;
    public AudioTrackRequest? Next;
    public double Volume { get; set; }

    /// <summary>The decode chain playing <see cref="Current"/>: each play and seek mints one.</summary>
    public long Chain;

    /// <summary>While &gt; 0, each PlayAsync throws StreamOpenException (stale-URL open denial).</summary>
    public int FailOpensRemaining;

    /// <summary>While &gt; 0, each PreloadNextAsync throws StreamOpenException (a URL
    /// googlevideo serves only the start of).</summary>
    public int FailPreloadOpensRemaining;

    public IReadOnlyList<string> Log
    {
        get { lock (_lock) return _log.ToArray(); }
    }

    public event Action<AudioPlayerEvent>? EventRaised;

    public Task PlayAsync(AudioTrackRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (FailOpensRemaining > 0)
        {
            FailOpensRemaining--;
            Add($"openfail:{request.Key}");
            throw new StreamOpenException($"open of '{request.DisplayName}' failed: 403");
        }

        Current = request;
        Chain++;
        Next = null;
        Add($"play:{request.Key}@{request.StartAt.TotalSeconds:0}");
        return Task.CompletedTask;
    }

    public Task PreloadNextAsync(AudioTrackRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (FailPreloadOpensRemaining > 0)
        {
            FailPreloadOpensRemaining--;
            Add($"preloadfail:{request.Key}");
            throw new StreamOpenException($"open of '{request.DisplayName}' failed: 403");
        }

        Next = request;
        Add($"preload:{request.Key}");
        return Task.CompletedTask;
    }

    public void ClearNext()
    {
        Next = null;
    }

    /// <summary>What was armed when the last seek began.</summary>
    public AudioTrackRequest? NextAtSeek;

    /// <summary>Runs inside SeekAsync, as engine events arriving while a seek prebuffers.</summary>
    public Action? DuringSeek;

    public void BeginCrossfade(TimeSpan duration) => Add($"crossfade:{duration.TotalSeconds:0}");

    /// <summary>Makes the next seek cut a fade the coordinator didn't start, dropping the armed track.</summary>
    public bool DropNextOnSeek;

    public Task<SeekResult> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        NextAtSeek = Next;
        DuringSeek?.Invoke();
        Add($"seek:{position.TotalSeconds:0}");
        Chain++;
        if (!DropNextOnSeek || Next is null)
            return Task.FromResult(new SeekResult(true, Chain));
        DropNextOnSeek = false;
        Next = null;
        return Task.FromResult(new SeekResult(false, Chain));
    }

    public void Pause() => Add("pause");
    public void Resume() => Add("resume");
    public void Stop() => Add("stop");

    public void Raise(AudioPlayerEvent e) => EventRaised?.Invoke(e);

    /// <param name="chain">The decode chain the tick was read from; the current one
    /// when null.</param>
    public void RaiseProgress(TimeSpan position, bool starved = false, long? chain = null)
    {
        if (Current is not null)
            Raise(new ProgressEvent(
                Current.Key, position, TimeSpan.FromSeconds(10), null, starved, chain ?? Chain));
    }

    private void Add(string entry)
    {
        lock (_lock) _log.Add(entry);
    }
}

internal static class TestTracks
{
    public static TrackInfo Track(string id, int durationSeconds = 100) => new()
    {
        VideoId = id,
        Title = $"Title {id}",
        Artists = [new("Artist")],
        Duration = TimeSpan.FromSeconds(durationSeconds),
    };

    /// <summary>Polls for an eventually-true condition (coordinator ended/fault paths hop threads).
    /// Async on purpose: the coordinator's continuations resume on xUnit's sync context,
    /// whose few workers a blocking poll would hold while waiting on them.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not reached.");
            await Task.Delay(10);
        }
    }
}
