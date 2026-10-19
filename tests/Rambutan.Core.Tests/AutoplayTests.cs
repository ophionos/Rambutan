using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;
using Rambutan.Core.Settings;

namespace Rambutan.Core.Tests;

/// <summary>Autoplay continuation: the policy, the queue's shape rules and
/// the coordinator's use of them, all with fakes.</summary>
public class AutoplayTests
{
    private static TrackInfo T(string id) => TestTracks.Track(id);

    private static IReadOnlyList<string> Ids(IEnumerable<TrackInfo> tracks) =>
        tracks.Select(t => t.VideoId).ToArray();

    [Theory]
    [InlineData(true, RepeatMode.Off, true)]
    [InlineData(false, RepeatMode.Off, false)]
    [InlineData(true, RepeatMode.All, false)]
    [InlineData(true, RepeatMode.One, false)]
    public void ShouldContinue_OnlyWithAutoplayOnAndRepeatOff(bool enabled, RepeatMode repeat, bool expected) =>
        Assert.Equal(expected, AutoplayPolicy.ShouldContinue(enabled, repeat));

    [Fact]
    public void Select_DropsSeedQueuedAndDuplicates_KeepsOrder()
    {
        var seed = T("s");
        var radio = new[] { T("s"), T("a"), T("q"), T("b"), T("a"), T("c") };

        var picked = AutoplayPolicy.SelectContinuation(seed, radio, new HashSet<string> { "q" }, new HashSet<string>());

        Assert.Equal(["a", "b", "c"], Ids(picked));
    }

    [Fact]
    public void Select_PrefersSongsNotPlayedThisSession()
    {
        var picked = AutoplayPolicy.SelectContinuation(
            T("s"), [T("s"), T("a"), T("b"), T("c")], new HashSet<string>(), new HashSet<string> { "a", "c" });

        Assert.Equal(["b"], Ids(picked));
    }

    [Fact]
    public void Select_FallsBackToPlayedSongs_WhenNothingNewCameBack()
    {
        var picked = AutoplayPolicy.SelectContinuation(
            T("s"), [T("s"), T("a"), T("b")], new HashSet<string>(), new HashSet<string> { "a", "b" });

        Assert.Equal(["a", "b"], Ids(picked));
    }

    [Fact]
    public void AppendAutoplay_AppliesOnlyWhileTheSeedIsLast()
    {
        var queue = new QueueModel();
        var seed = T("a");
        queue.SetQueue([seed]);
        queue.Enqueue(T("b")); // the user queued more while the radio was coming

        Assert.Equal(0, queue.AppendAutoplay(seed, [T("x")]));
        Assert.Equal(["a", "b"], Ids(queue.Items));
    }

    [Fact]
    public void AppendAutoplay_MarksTracks_AndSkipsQueuedOnes()
    {
        var queue = new QueueModel();
        var seed = T("b");
        queue.SetQueue([T("a"), seed], 1);

        Assert.Equal(2, queue.AppendAutoplay(seed, [T("a"), T("x"), T("y")]));

        Assert.Equal(["a", "b", "x", "y"], Ids(queue.Items));
        Assert.False(queue.IsAutoplay(queue.Items[1]));
        Assert.True(queue.IsAutoplay(queue.Items[2]));
        Assert.Equal([2, 3], queue.Snapshot().AutoplayIndices);
    }

    [Fact]
    public void Enqueue_GoesAheadOfUpcomingAutoplayTracks()
    {
        var queue = new QueueModel();
        var seed = T("a");
        queue.SetQueue([seed]);
        queue.AppendAutoplay(seed, [T("x"), T("y")]);

        queue.Enqueue(T("mine"));

        Assert.Equal(["a", "mine", "x", "y"], Ids(queue.Items));
        Assert.Equal([2, 3], queue.Snapshot().AutoplayIndices);
    }

    [Fact]
    public void EnqueueRange_KeepsTheAlbumsOrder_AheadOfAutoplay_WithOneChange()
    {
        var queue = new QueueModel();
        var seed = T("a");
        queue.SetQueue([seed]);
        queue.AppendAutoplay(seed, [T("x"), T("y")]);
        var changes = 0;
        queue.Changed += () => changes++;

        queue.EnqueueRange([T("1"), T("2"), T("3")]);

        Assert.Equal(["a", "1", "2", "3", "x", "y"], Ids(queue.Items));
        Assert.Equal([4, 5], queue.Snapshot().AutoplayIndices);
        Assert.Equal(1, changes);

        queue.EnqueueRange([]);
        Assert.Equal(1, changes); // nothing added, nothing to tell
    }

    [Fact]
    public void EnqueueRange_WithShuffleOn_KeepsTheAlbumsOrderInTheOriginalOrder()
    {
        var queue = new QueueModel(new Random(7));
        var seed = T("a");
        queue.SetQueue([seed]);
        queue.AppendAutoplay(seed, [T("x"), T("y")]);
        queue.SetShuffle(true);

        queue.EnqueueRange([T("1"), T("2"), T("3")]);
        queue.SetShuffle(false);

        Assert.Equal(["a", "1", "2", "3", "x", "y"], Ids(queue.Items));
    }

    [Fact]
    public void Enqueue_WithShuffleOn_LandsBeforeAutoplayInTheOriginalOrderToo()
    {
        var queue = new QueueModel(new Random(7));
        var seed = T("a");
        queue.SetQueue([seed]);
        queue.SetShuffle(true);
        queue.AppendAutoplay(seed, [T("x"), T("y"), T("z")]);

        queue.Enqueue(T("mine"));
        Assert.Equal("mine", queue.Items[1].VideoId);

        queue.SetShuffle(false);
        Assert.Equal(["a", "mine", "x", "y", "z"], Ids(queue.Items));
    }

    [Fact]
    public void MovingAnAutoplayTrack_MakesItTheUsers()
    {
        var queue = new QueueModel();
        var seed = T("a");
        queue.SetQueue([seed]);
        queue.AppendAutoplay(seed, [T("x"), T("y")]);

        queue.Move(2, 1);

        Assert.Equal(["a", "y", "x"], Ids(queue.Items));
        Assert.False(queue.IsAutoplay(queue.Items[1]));
        Assert.True(queue.IsAutoplay(queue.Items[2]));
    }

    [Fact]
    public void NewQueue_DropsMarks_AndRestoreBringsThemBack()
    {
        var queue = new QueueModel();
        var seed = T("a");
        queue.SetQueue([seed]);
        queue.AppendAutoplay(seed, [T("x")]);
        var saved = queue.Snapshot();

        queue.SetQueue([T("n")]);
        Assert.Null(queue.Snapshot().AutoplayIndices);

        queue.Restore(saved);
        Assert.True(queue.IsAutoplay(queue.Items[1]));
    }

    private readonly FakeStreamSource _streams = new();
    private readonly FakeAudioPlayer _player = new();
    private readonly QueueModel _queue = new();
    private readonly PlaybackSettings _settings = new();
    private readonly FakeRadioSource _radio = new();

    private PlaybackCoordinator Create() => new(_streams, _player, _queue, _settings, _radio);

    private void End() => _player.Raise(new PlaybackEndedEvent(_player.Current!.Key));

    [Fact]
    public async Task QueueEnd_ContinuesWithRadioFromTheLastTrack()
    {
        _radio.Tracks["b"] = [T("b"), T("r1"), T("r2")];
        using var coordinator = Create();
        await coordinator.PlayQueueAsync([T("a"), T("b")], 1);

        End();

        await TestTracks.WaitUntilAsync(() => coordinator.NowPlaying?.VideoId == "r1");
        Assert.Equal(["a", "b", "r1", "r2"], Ids(_queue.Items));
        Assert.True(_queue.IsAutoplay(_queue.Items[2]));
        Assert.Equal(["b"], _radio.Requests);
    }

    [Fact]
    public async Task QueueEnd_SkipsSongsAlreadyPlayedThisSession()
    {
        _radio.Tracks["b"] = [T("b"), T("a"), T("r1")];
        using var coordinator = Create();
        await coordinator.PlayQueueAsync([T("a")]);
        await coordinator.PlayQueueAsync([T("b")]); // "a" no longer queued, but heard

        End();

        await TestTracks.WaitUntilAsync(() => coordinator.NowPlaying?.VideoId == "r1");
        Assert.Equal(["b", "r1"], Ids(_queue.Items));
    }

    [Fact]
    public async Task AutoplayOff_QueueEndStops()
    {
        _settings.Autoplay = false;
        _radio.Tracks["a"] = [T("a"), T("r1")];
        using var coordinator = Create();
        await coordinator.PlayQueueAsync([T("a")]);

        End();

        await TestTracks.WaitUntilAsync(() => coordinator.State == PlaybackState.Stopped);
        Assert.Empty(_radio.Requests);
    }

    [Fact]
    public async Task RepeatOn_NeverAutoplays()
    {
        _radio.Tracks["a"] = [T("a"), T("r1")];
        using var coordinator = Create();
        await coordinator.PlayQueueAsync([T("a")]);
        _queue.Repeat = RepeatMode.One;

        await coordinator.NextAsync();

        Assert.Empty(_radio.Requests);
        Assert.Single(_queue.Items);
    }

    [Fact]
    public async Task FetchFailure_EndsTheQueueWithANotification()
    {
        _radio.Fail = true;
        var messages = new List<PlaybackNotice>();
        using var coordinator = Create();
        coordinator.Notification += m => { lock (messages) messages.Add(m); };
        await coordinator.PlayQueueAsync([T("a")]);

        End();

        await TestTracks.WaitUntilAsync(() => coordinator.State == PlaybackState.Stopped);
        lock (messages)
            Assert.Contains(messages, m => m.Kind == PlaybackNoticeKind.AutoplayFailed && m.Subject == "Title a");
    }

    [Fact]
    public async Task NothingNew_EndsTheQueueWithANotification()
    {
        _radio.Tracks["a"] = [T("a")];
        var messages = new List<PlaybackNotice>();
        using var coordinator = Create();
        coordinator.Notification += m => { lock (messages) messages.Add(m); };
        await coordinator.PlayQueueAsync([T("a")]);

        End();

        await TestTracks.WaitUntilAsync(() => coordinator.State == PlaybackState.Stopped);
        lock (messages)
            Assert.Contains(messages, m => m.Kind == PlaybackNoticeKind.AutoplayEmpty && m.Subject == "Title a");
    }

    [Fact]
    public async Task FetchAhead_QueuesAndArmsTheFirstAutoplayTrack_Once()
    {
        _radio.Tracks["a"] = [T("a"), T("r1")];
        using var coordinator = Create();
        await coordinator.PlayQueueAsync([T("a")]);

        _player.RaiseProgress(TimeSpan.FromSeconds(3));
        Assert.Empty(_radio.Requests); // not yet: a shelf song's own radio may still arrive

        _player.RaiseProgress(TimeSpan.FromSeconds(10));
        _player.RaiseProgress(TimeSpan.FromSeconds(11));

        await TestTracks.WaitUntilAsync(() => _player.Next?.Key.StartsWith("r1#") == true);
        Assert.Equal(["a", "r1"], Ids(_queue.Items));
        Assert.Equal(["a"], _radio.Requests);
    }

    [Fact]
    public async Task FetchAhead_DoesNothingWhenTheQueueDoesntEndHere()
    {
        using var coordinator = Create();
        await coordinator.PlayQueueAsync([T("a"), T("b")]);
        await TestTracks.WaitUntilAsync(() => _player.Next is not null);

        _player.RaiseProgress(TimeSpan.FromSeconds(20));

        Assert.Empty(_radio.Requests);
    }

    [Fact]
    public async Task FetchAheadFailure_IsRetriedAtTheQueuesEnd()
    {
        _radio.Fail = true;
        _radio.Tracks["a"] = [T("a"), T("r1")];
        using var coordinator = Create();
        await coordinator.PlayQueueAsync([T("a")]);
        _player.RaiseProgress(TimeSpan.FromSeconds(15));
        await TestTracks.WaitUntilAsync(() => _radio.Requests.Count == 1);

        _radio.Fail = false;
        End();

        await TestTracks.WaitUntilAsync(() => coordinator.NowPlaying?.VideoId == "r1");
        Assert.Equal(2, _radio.Requests.Count);
    }

    [Fact]
    public async Task NextOnTheLastTrack_ContinuesWithAutoplay()
    {
        _radio.Tracks["a"] = [T("a"), T("r1")];
        using var coordinator = Create();
        await coordinator.PlayQueueAsync([T("a")]);

        await coordinator.NextAsync();

        Assert.Equal("r1", coordinator.NowPlaying?.VideoId);
    }

    [Fact]
    public async Task WithoutARadioSource_QueueEndStopsAsBefore()
    {
        using var coordinator = new PlaybackCoordinator(_streams, _player, _queue, _settings);
        await coordinator.PlayQueueAsync([T("a")]);

        End();

        await TestTracks.WaitUntilAsync(() => coordinator.State == PlaybackState.Stopped);
    }
}

internal sealed class FakeRadioSource : IRadioSource
{
    private readonly List<string> _requests = [];

    public readonly Dictionary<string, TrackInfo[]> Tracks = [];
    public volatile bool Fail;

    public IReadOnlyList<string> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    public Task<IReadOnlyList<TrackInfo>> GetRadioAsync(string videoId, CancellationToken ct = default)
    {
        lock (_requests) _requests.Add(videoId);
        if (Fail)
            return Task.FromException<IReadOnlyList<TrackInfo>>(new HttpRequestException("offline"));
        return Task.FromResult<IReadOnlyList<TrackInfo>>(Tracks.GetValueOrDefault(videoId) ?? []);
    }
}
