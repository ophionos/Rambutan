using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;
using Rambutan.Core.Settings;

namespace Rambutan.Core.Tests;

/// <summary>The network goes away while a track plays: the track waits for it and
/// resumes where it stopped, instead of being skipped.</summary>
public class NetworkDropTests
{
    private readonly FakeStreamSource _streams = new();
    private readonly FakeAudioPlayer _player = new();
    private readonly QueueModel _queue = new();
    private readonly PlaybackSettings _settings = new();
    private readonly List<PlaybackNotice> _notices = [];
    private readonly List<TimeSpan> _delays = [];

    /// <summary>Each <see cref="PlaybackCoordinator.NetworkWaitChanged"/>: the
    /// waiting track's id, or null when the wait ended.</summary>
    private readonly List<string?> _waits = [];

    /// <summary>What a wait does; by default it returns at once.</summary>
    private Func<CancellationToken, Task> _wait = _ => Task.CompletedTask;

    /// <summary>Runs after each wait, before the next try.</summary>
    private Action<int> _afterWait = _ => { };

    private PlaybackCoordinator CreateCoordinator()
    {
        var coordinator = new PlaybackCoordinator(
            _streams, _player, _queue, _settings, delay: async (wait, ct) =>
            {
                lock (_delays) _delays.Add(wait);
                await _wait(ct);
                _afterWait(_delays.Count);
            });
        coordinator.Notification += n =>
        {
            lock (_notices) _notices.Add(n);
        };
        coordinator.NetworkWaitChanged += track =>
        {
            lock (_waits) _waits.Add(track?.VideoId);
        };
        return coordinator;
    }

    private static IReadOnlyList<TrackInfo> Tracks(params string[] ids) =>
        ids.Select(id => TestTracks.Track(id)).ToArray();

    private bool Noticed(PlaybackNoticeKind kind)
    {
        lock (_notices) return _notices.Any(n => n.Kind == kind);
    }

    private string?[] Waits()
    {
        lock (_waits) return [.. _waits];
    }

    private bool Waiting() => Waits() is [.., not null];

    /// <summary>Plays t1 to 42 s, then the network drops and the decoder fails.</summary>
    private async Task<string> DropAt42Async(PlaybackCoordinator coordinator, params string[] queue)
    {
        await coordinator.PlayQueueAsync(Tracks(queue.Length > 0 ? queue : ["t1", "t2"]));
        _player.RaiseProgress(TimeSpan.FromSeconds(42));
        var faulted = _player.Current!.Key;
        _streams.Offline = true;
        _player.Raise(new PlayerFaultedEvent(faulted, "read frame failed: Input/output error (-5)"));
        return faulted;
    }

    [Fact]
    public async Task Drop_WaitsForTheNetwork_AndResumesWhereItStopped()
    {
        _afterWait = count =>
        {
            if (count == 3)
                _streams.Offline = false;
        };
        using var coordinator = CreateCoordinator();

        var faulted = await DropAt42Async(coordinator);

        await TestTracks.WaitUntilAsync(() => _player.Current!.Key != faulted);
        Assert.StartsWith("t1#", _player.Current!.Key);
        Assert.Equal(TimeSpan.FromSeconds(42), _player.Current.StartAt);
        Assert.Equal("t1", coordinator.NowPlaying!.VideoId);
        Assert.Equal(PlaybackCoordinator.NetworkRetryDelays.Take(3), _delays);
        Assert.Equal(["t1", null], Waits());
        Assert.Null(coordinator.TrackWaitingForNetwork);
        Assert.False(Noticed(PlaybackNoticeKind.TrackSkipped));
    }

    [Fact]
    public async Task Drop_PlaysOutTheBuffer_AndShowsBufferingWhenItRunsOut()
    {
        var waiting = new TaskCompletionSource();
        _wait = ct => waiting.Task.WaitAsync(ct);
        using var coordinator = CreateCoordinator();
        var faulted = await DropAt42Async(coordinator);
        await TestTracks.WaitUntilAsync(Waiting);

        _player.Raise(new ProgressEvent(faulted, TimeSpan.FromSeconds(50), TimeSpan.Zero, null, true));
        Assert.Equal(PlaybackState.Buffering, coordinator.State);

        _streams.Offline = false;
        waiting.SetResult();
        await TestTracks.WaitUntilAsync(() => _player.Current!.Key != faulted);
        Assert.Equal(TimeSpan.FromSeconds(50), _player.Current!.StartAt);
        Assert.Equal(PlaybackState.Playing, coordinator.State);
    }

    [Fact]
    public async Task Drop_WaitsLongerBetweenTries_UpToTheLast()
    {
        _afterWait = count =>
        {
            if (count == 6)
                _streams.Offline = false;
        };
        using var coordinator = CreateCoordinator();

        var faulted = await DropAt42Async(coordinator);

        await TestTracks.WaitUntilAsync(() => _player.Current!.Key != faulted);
        var last = PlaybackCoordinator.NetworkRetryDelays[^1];
        Assert.Equal([.. PlaybackCoordinator.NetworkRetryDelays, last, last], _delays);
    }

    [Fact]
    public async Task SeekWhileWaiting_MovesWhereTheTrackResumes()
    {
        var waiting = new TaskCompletionSource();
        _wait = ct => waiting.Task.WaitAsync(ct);
        using var coordinator = CreateCoordinator();
        var faulted = await DropAt42Async(coordinator);
        await TestTracks.WaitUntilAsync(Waiting);

        // Returns at once: the wait holds the gate, and a seek must not queue behind it.
        await coordinator.SeekAsync(TimeSpan.FromSeconds(70)).WaitAsync(TimeSpan.FromSeconds(5));
        _streams.Offline = false;
        waiting.SetResult();

        await TestTracks.WaitUntilAsync(() => _player.Current!.Key != faulted);
        Assert.Equal(TimeSpan.FromSeconds(70), _player.Current!.StartAt);
    }

    [Fact]
    public async Task PreviousWhileWaiting_RestartsTheTrackWhenTheNetworkIsBack()
    {
        var waiting = new TaskCompletionSource();
        _wait = ct => waiting.Task.WaitAsync(ct);
        using var coordinator = CreateCoordinator();
        var faulted = await DropAt42Async(coordinator);
        await TestTracks.WaitUntilAsync(Waiting);

        await coordinator.PreviousAsync().WaitAsync(TimeSpan.FromSeconds(5));
        _streams.Offline = false;
        waiting.SetResult();

        await TestTracks.WaitUntilAsync(() => _player.Current!.Key != faulted);
        Assert.StartsWith("t1#", _player.Current!.Key);
        Assert.Equal(TimeSpan.Zero, _player.Current.StartAt);
        Assert.DoesNotContain(_player.Log, l => l.StartsWith("seek:"));
    }

    [Fact]
    public async Task ANetworkThatComesBackHalfway_IsTriedAgain()
    {
        // The resolve gets through, but the stream doesn't open yet.
        _afterWait = count =>
        {
            if (count != 1)
                return;
            _streams.Offline = false;
            _player.FailOpensRemaining = 1;
        };
        using var coordinator = CreateCoordinator();

        var faulted = await DropAt42Async(coordinator);

        await TestTracks.WaitUntilAsync(() => _player.Current!.Key != faulted);
        Assert.Equal(TimeSpan.FromSeconds(42), _player.Current!.StartAt);
        Assert.Equal(2, _delays.Count);
        Assert.False(Noticed(PlaybackNoticeKind.TrackSkipped));
    }

    [Fact]
    public async Task PauseWhileWaiting_StaysPausedWhenTheNetworkIsBack()
    {
        var waiting = new TaskCompletionSource();
        _wait = ct => waiting.Task.WaitAsync(ct);
        using var coordinator = CreateCoordinator();
        var faulted = await DropAt42Async(coordinator);
        await TestTracks.WaitUntilAsync(Waiting);

        coordinator.Pause();
        _streams.Offline = false;
        waiting.SetResult();

        await TestTracks.WaitUntilAsync(() => _player.Log[^1] == "pause" && _player.Current!.Key != faulted);
        Assert.Equal(PlaybackState.Paused, coordinator.State);
    }

    [Fact]
    public async Task NextWhileWaiting_EndsTheWait()
    {
        _wait = ct => Task.Delay(Timeout.Infinite, ct);
        using var coordinator = CreateCoordinator();
        await DropAt42Async(coordinator);
        await TestTracks.WaitUntilAsync(Waiting);

        _streams.Offline = false;
        await coordinator.NextAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
        Assert.Single(_delays);
        await TestTracks.WaitUntilAsync(() => !Waiting());
        Assert.Equal(["t1", null], Waits());
    }

    [Fact]
    public async Task NoNetworkForTheWholeWait_MovesToWhatPlaysOffline()
    {
        _streams.AvailableOffline.Add("t3");
        using var coordinator = CreateCoordinator();

        await DropAt42Async(coordinator, "t1", "t2", "t3");

        await TestTracks.WaitUntilAsync(() => coordinator.NowPlaying?.VideoId == "t3");
        Assert.True(_delays.Aggregate(TimeSpan.Zero, (a, b) => a + b) >= PlaybackCoordinator.NetworkWaitLimit);
        Assert.True(Noticed(PlaybackNoticeKind.SkippedOffline));
    }

    [Fact]
    public async Task NoNetworkForTheWholeWait_AndNothingOffline_Stops()
    {
        using var coordinator = CreateCoordinator();

        await DropAt42Async(coordinator);

        await TestTracks.WaitUntilAsync(() => coordinator.State == PlaybackState.Stopped);
        Assert.True(Noticed(PlaybackNoticeKind.StoppedOffline));
        Assert.Equal(0, _queue.CurrentIndex);
        Assert.Equal(["t1", null], Waits());
    }

    [Fact]
    public async Task ASecondDrop_InTheSameTrack_WaitsAgain()
    {
        _afterWait = _ => _streams.Offline = false;
        using var coordinator = CreateCoordinator();
        var first = await DropAt42Async(coordinator);
        await TestTracks.WaitUntilAsync(() => _player.Current!.Key != first);

        var second = _player.Current!.Key;
        _streams.Offline = true;
        _player.Raise(new PlayerFaultedEvent(second, "read frame failed"));

        await TestTracks.WaitUntilAsync(() => _player.Current!.Key != second);
        Assert.Equal("t1", coordinator.NowPlaying!.VideoId);
        Assert.False(Noticed(PlaybackNoticeKind.TrackSkipped));
        Assert.Equal(2, _delays.Count);
        Assert.Equal(["t1", null, "t1", null], Waits());
    }

    [Fact]
    public async Task TheWait_IsStateForAsLongAsItLasts_SeeksAndPreviousIncluded()
    {
        var waiting = new TaskCompletionSource();
        _wait = ct => waiting.Task.WaitAsync(ct);
        using var coordinator = CreateCoordinator();
        var faulted = await DropAt42Async(coordinator);
        await TestTracks.WaitUntilAsync(Waiting);
        Assert.Equal("t1", coordinator.TrackWaitingForNetwork?.VideoId);

        // Neither ends the wait: both move where the track resumes.
        await coordinator.SeekAsync(TimeSpan.FromSeconds(70)).WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.PreviousAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("t1", coordinator.TrackWaitingForNetwork?.VideoId);
        Assert.Equal(["t1"], Waits());

        _streams.Offline = false;
        waiting.SetResult();
        await TestTracks.WaitUntilAsync(() => _player.Current!.Key != faulted);
        await TestTracks.WaitUntilAsync(() => !Waiting());
        Assert.Equal(["t1", null], Waits());
        Assert.Null(coordinator.TrackWaitingForNetwork);
    }

    [Fact]
    public async Task AFaultThatRecoversAtOnce_IsNoWait()
    {
        // An expired URL: the first re-resolve plays on, with no network lost.
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));
        _player.RaiseProgress(TimeSpan.FromSeconds(42));
        var faulted = _player.Current!.Key;
        _player.Raise(new PlayerFaultedEvent(faulted, "HTTP error 403 Forbidden"));

        await TestTracks.WaitUntilAsync(() => _player.Current!.Key != faulted);
        Assert.Empty(Waits());
        Assert.Null(coordinator.TrackWaitingForNetwork);
    }
}
