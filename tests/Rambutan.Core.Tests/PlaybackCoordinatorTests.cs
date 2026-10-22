using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;
using Rambutan.Core.Settings;

namespace Rambutan.Core.Tests;

public class PlaybackCoordinatorTests
{
    private readonly FakeStreamSource _streams = new();
    private readonly FakeAudioPlayer _player = new();
    private readonly QueueModel _queue = new();
    private readonly PlaybackSettings _settings = new();

    private PlaybackCoordinator CreateCoordinator() =>
        new(_streams, _player, _queue, _settings);

    private static IReadOnlyList<TrackInfo> Tracks(params string[] ids) =>
        ids.Select(id => TestTracks.Track(id)).ToArray();

    /// <summary>Preload runs detached (after the play command releases the gate),
    /// so tests that depend on an armed successor wait for it.</summary>
    private Task WaitForPreloadAsync() => TestTracks.WaitUntilAsync(() => _player.Next is not null);

    [Fact]
    public async Task PlayQueue_PlaysFirst_AndPreloadsSecond()
    {
        using var coordinator = CreateCoordinator();

        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));

        Assert.Equal(PlaybackState.Playing, coordinator.State);
        Assert.Equal("t1", coordinator.NowPlaying!.VideoId);
        Assert.StartsWith("t1#", _player.Current!.Key);
        await WaitForPreloadAsync();
        Assert.StartsWith("t2#", _player.Next!.Key);
    }

    [Fact]
    public async Task PlayAfterCurrent_PlaysTheFirst_AndKeepsTheRestOfTheQueue()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));

        await coordinator.PlayAfterCurrentAsync(Tracks("n1", "n2"));

        Assert.Equal("n1", coordinator.NowPlaying!.VideoId);
        Assert.Equal(["t1", "n1", "n2", "t2"], _queue.Snapshot().Tracks.Select(t => t.VideoId));
        Assert.Equal(1, _queue.CurrentIndex);
    }

    [Fact]
    public async Task PlayAfterCurrent_OnAnEmptyQueue_PlaysTheFirst()
    {
        using var coordinator = CreateCoordinator();

        await coordinator.PlayAfterCurrentAsync(Tracks("n1", "n2"));

        Assert.Equal("n1", coordinator.NowPlaying!.VideoId);
        Assert.Equal(["n1", "n2"], _queue.Snapshot().Tracks.Select(t => t.VideoId));
    }

    [Fact]
    public async Task JumpTo_StartsFromTheGivenPosition()
    {
        using var coordinator = CreateCoordinator();
        _queue.SetQueue(Tracks("t1", "t2"), 0);

        await coordinator.JumpToAsync(1, TimeSpan.FromSeconds(42));

        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
        Assert.Equal(TimeSpan.FromSeconds(42), _player.Current!.StartAt);
    }

    [Fact]
    public async Task Crossfade_BeginsOnceInsideWindow()
    {
        _settings.CrossfadeEnabled = true;
        _settings.CrossfadeSeconds = 8;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2")); // 100 s tracks
        await WaitForPreloadAsync();

        _player.RaiseProgress(TimeSpan.FromSeconds(80));
        Assert.DoesNotContain(_player.Log, entry => entry.StartsWith("crossfade"));

        _player.RaiseProgress(TimeSpan.FromSeconds(93));
        _player.RaiseProgress(TimeSpan.FromSeconds(94));

        // Duration is clamped to the runway left when the window is entered (7 s here).
        Assert.Equal(1, _player.Log.Count(entry => entry.StartsWith("crossfade")));
        Assert.Contains("crossfade:7", _player.Log);
    }

    [Fact]
    public async Task Crossfade_ClampsToRemainingTime()
    {
        _settings.CrossfadeEnabled = true;
        _settings.CrossfadeSeconds = 8;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));
        await WaitForPreloadAsync();

        _player.RaiseProgress(TimeSpan.FromSeconds(97)); // 3 s left, 8 s window

        Assert.Contains("crossfade:3", _player.Log);
    }

    [Fact]
    public async Task CrossfadeDisabled_NeverBegins()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));

        _player.RaiseProgress(TimeSpan.FromSeconds(99));

        Assert.DoesNotContain(_player.Log, entry => entry.StartsWith("crossfade"));
    }

    [Fact]
    public async Task Preload_WhoseOpenFails_ResolvesAgain_AndArmsTheFreshUrl()
    {
        // Googlevideo refuses some fresh URLs past their first megabyte. The
        // retry happens while the current track plays, so the fade survives.
        _player.FailPreloadOpensRemaining = 1;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));
        await WaitForPreloadAsync();

        Assert.StartsWith("t2#", _player.Next!.Key);
        Assert.Contains(_player.Log, entry => entry.StartsWith("preloadfail:t2#"));
        Assert.Equal([("t2", false), ("t2", true)], _streams.Calls.Where(call => call.VideoId == "t2"));
    }

    [Fact]
    public async Task Preload_WhoseOpenFailsTwice_LeavesNothingArmed()
    {
        _player.FailPreloadOpensRemaining = 2;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));
        await TestTracks.WaitUntilAsync(() => _player.Log.Count(entry => entry.StartsWith("preloadfail:")) == 2);

        Assert.Null(_player.Next);
        Assert.Equal(2, _streams.Calls.Count(call => call.VideoId == "t2"));
    }

    [Fact]
    public async Task Promotion_AdvancesQueue_AndPreloadsFollowing()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2", "t3"));
        await WaitForPreloadAsync();

        var promotedKey = _player.Next!.Key;
        _player.Current = _player.Next; // engine promoted internally
        _player.Raise(new TrackPromotedEvent(promotedKey));

        await TestTracks.WaitUntilAsync(() => _player.Next?.Key.StartsWith("t3#") == true);
        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
        Assert.Equal(1, _queue.CurrentIndex);
    }

    [Fact]
    public async Task Ended_WithoutPreload_PlaysNextFromScratch()
    {
        _streams.FailAlways.Add("t2"); // poison preload for t2...
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2", "t3"));
        await TestTracks.WaitUntilAsync(() => _streams.Calls.Contains(("t2", false)));
        Assert.Null(_player.Next); // preload of t2 failed
        _streams.FailAlways.Remove("t2"); // ...but it plays fine when asked directly

        _player.Raise(new PlaybackEndedEvent(_player.Current!.Key));

        await TestTracks.WaitUntilAsync(() => coordinator.NowPlaying?.VideoId == "t2");
        Assert.Equal(PlaybackState.Playing, coordinator.State);
    }

    [Fact]
    public async Task Ended_AtQueueEnd_Stops()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1"));

        _player.Raise(new PlaybackEndedEvent(_player.Current!.Key));

        await TestTracks.WaitUntilAsync(() => coordinator.State == PlaybackState.Stopped);
        Assert.Null(coordinator.NowPlaying);
    }

    [Fact]
    public async Task RepeatOne_PreloadsSameTrack()
    {
        _queue.Repeat = RepeatMode.One;
        using var coordinator = CreateCoordinator();

        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));

        await WaitForPreloadAsync();
        Assert.StartsWith("t1#", _player.Next!.Key);
    }

    [Fact]
    public async Task TransientResolveFailure_RetriesWithBypass()
    {
        _streams.FailOnFirstTry.Add("t1");
        using var coordinator = CreateCoordinator();

        await coordinator.PlayQueueAsync(Tracks("t1"));

        Assert.Equal(PlaybackState.Playing, coordinator.State);
        Assert.Contains(("t1", true), _streams.Calls);
    }

    [Fact]
    public async Task PermanentResolveFailure_SkipsToNext_WithNotification()
    {
        _streams.FailAlways.Add("t1");
        var notifications = new List<PlaybackNotice>();
        using var coordinator = CreateCoordinator();
        coordinator.Notification += notifications.Add;

        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));

        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
        Assert.Contains(notifications, n => n.Subject == "Title t1");
    }

    [Fact]
    public async Task Offline_SkipsStraightToTheNextTrackThatPlaysOffline()
    {
        _streams.Offline = true;
        _streams.AvailableOffline.Add("t4");
        var notifications = new List<PlaybackNotice>();
        using var coordinator = CreateCoordinator();
        coordinator.Notification += notifications.Add;

        await coordinator.PlayQueueAsync(Tracks("t1", "t2", "t3", "t4"));

        Assert.Equal("t4", coordinator.NowPlaying!.VideoId);
        Assert.Equal(PlaybackState.Playing, coordinator.State);
        // One attempt at t1, no bypass retry, and nothing tried in between.
        Assert.Equal([("t1", false), ("t4", false)], _streams.Calls.Take(2));
        Assert.DoesNotContain(_streams.Calls, c => c.VideoId is "t2" or "t3");
        var notice = Assert.Single(notifications);
        Assert.Equal(new PlaybackNotice(PlaybackNoticeKind.SkippedOffline, "Title t1"), notice);
    }

    [Fact]
    public async Task Offline_WithNothingAheadThatPlaysOffline_StopsOnTheTrack()
    {
        _streams.Offline = true;
        _streams.AvailableOffline.Add("t1"); // behind the start, and no Repeat All
        var notifications = new List<PlaybackNotice>();
        using var coordinator = CreateCoordinator();
        coordinator.Notification += notifications.Add;

        await coordinator.PlayQueueAsync(Tracks("t1", "t2", "t3"), startIndex: 1);

        Assert.Equal(PlaybackState.Stopped, coordinator.State);
        Assert.Equal(1, _queue.CurrentIndex);
        Assert.Equal([("t2", false)], _streams.Calls);
        var notice = Assert.Single(notifications);
        Assert.Equal(new PlaybackNotice(PlaybackNoticeKind.StoppedOffline, "Title t2"), notice);
    }

    [Fact]
    public async Task Offline_UnderRepeatAll_WrapsRoundToATrackThatPlaysOffline()
    {
        _streams.Offline = true;
        _streams.AvailableOffline.Add("t1");
        _queue.Repeat = RepeatMode.All;
        using var coordinator = CreateCoordinator();

        await coordinator.PlayQueueAsync(Tracks("t1", "t2", "t3"), startIndex: 1);

        Assert.Equal("t1", coordinator.NowPlaying!.VideoId);
        Assert.Equal(0, _queue.CurrentIndex);
    }

    [Fact]
    public async Task Offline_AtATrackEnd_SkipsWhatNeedsTheNetwork()
    {
        _streams.AvailableOffline.UnionWith(["t1", "t3"]);
        _streams.Offline = true;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2", "t3"));

        _player.Raise(new PlaybackEndedEvent(_player.Current!.Key));

        await TestTracks.WaitUntilAsync(() => coordinator.NowPlaying?.VideoId == "t3");
        Assert.Equal(PlaybackState.Playing, coordinator.State);
    }

    [Fact]
    public async Task OpenFailure_RetriesOnceWithFreshUrl()
    {
        _player.FailOpensRemaining = 1;
        using var coordinator = CreateCoordinator();

        await coordinator.PlayQueueAsync(Tracks("t1"));

        Assert.Equal(PlaybackState.Playing, coordinator.State);
        Assert.Equal("t1", coordinator.NowPlaying!.VideoId);
        Assert.Contains(("t1", true), _streams.Calls);
    }

    [Fact]
    public async Task PersistentOpenFailure_SkipsToNext()
    {
        _player.FailOpensRemaining = 2; // both attempts for t1 fail
        var notifications = new List<PlaybackNotice>();
        using var coordinator = CreateCoordinator();
        coordinator.Notification += notifications.Add;

        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));

        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
        Assert.Contains(notifications, n => n.Subject == "Title t1");
    }

    [Fact]
    public async Task Fault_RecoversOnce_ByReresolvingAtPosition()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));
        _player.RaiseProgress(TimeSpan.FromSeconds(42));
        var faultedKey = _player.Current!.Key;

        _player.Raise(new PlayerFaultedEvent(faultedKey, "URL expired"));

        await TestTracks.WaitUntilAsync(() =>
            _player.Current!.Key != faultedKey && _player.Current.Key.StartsWith("t1#"));
        Assert.Equal(TimeSpan.FromSeconds(42), _player.Current!.StartAt);
        Assert.Contains(("t1", true), _streams.Calls);
        Assert.Equal("t1", coordinator.NowPlaying!.VideoId);
    }

    [Fact]
    public async Task SecondFault_SkipsToNext()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));

        _player.Raise(new PlayerFaultedEvent(_player.Current!.Key, "boom"));
        await TestTracks.WaitUntilAsync(() => _player.Current!.Key.StartsWith("t1#") &&
                                   _player.Log.Count(l => l.StartsWith("play:t1")) == 2);

        _player.Raise(new PlayerFaultedEvent(_player.Current!.Key, "boom again"));

        await TestTracks.WaitUntilAsync(() => coordinator.NowPlaying?.VideoId == "t2");
    }

    [Fact]
    public async Task Starvation_MapsToBuffering_AndRecovers()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1"));

        _player.RaiseProgress(TimeSpan.FromSeconds(10), starved: true);
        Assert.Equal(PlaybackState.Buffering, coordinator.State);

        _player.RaiseProgress(TimeSpan.FromSeconds(10), starved: false);
        Assert.Equal(PlaybackState.Playing, coordinator.State);
    }

    [Fact]
    public async Task PauseResume_DrivesPlayerAndState()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1"));

        coordinator.Pause();
        Assert.Equal(PlaybackState.Paused, coordinator.State);
        Assert.Contains("pause", _player.Log);

        coordinator.Resume();
        Assert.Equal(PlaybackState.Playing, coordinator.State);
        Assert.Contains("resume", _player.Log);
    }

    [Fact]
    public async Task Previous_RestartsWhenPastThreshold()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"), startIndex: 1);
        _player.RaiseProgress(TimeSpan.FromSeconds(30));

        await coordinator.PreviousAsync();

        Assert.Contains("seek:0", _player.Log);
        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
    }

    [Fact]
    public async Task Previous_GoesBackEarlyInTrack()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"), startIndex: 1);
        _player.RaiseProgress(TimeSpan.FromSeconds(1));

        await coordinator.PreviousAsync();

        Assert.Equal("t1", coordinator.NowPlaying!.VideoId);
    }

    [Fact]
    public async Task Next_AtQueueEnd_KeepsPlaying()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1"));

        await coordinator.NextAsync();

        Assert.Equal(PlaybackState.Playing, coordinator.State);
        Assert.Equal("t1", coordinator.NowPlaying!.VideoId);
    }

    [Fact]
    public async Task StaleProgress_FromOldTrack_IsIgnored()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));
        var oldKey = _player.Current!.Key;

        await coordinator.NextAsync();
        _player.Raise(new ProgressEvent(oldKey, TimeSpan.FromSeconds(5), TimeSpan.Zero, null, false));

        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
    }

    [Fact]
    public async Task Ended_DuringManualNext_AdvancesOnlyOnce()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2", "t3"));
        await WaitForPreloadAsync(); // settle the detached preload before poisoning t2
        var endedKey = _player.Current!.Key;

        var gate = _streams.Block("t2");
        var next = coordinator.NextAsync();               // advances to t2, blocks resolving it
        _player.Raise(new PlaybackEndedEvent(endedKey));  // t1 drains during the load
        gate.SetResult();
        await next;

        // The ended reaction must drop out (a newer command superseded it), not
        // advance the queue a second time to t3.
        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
        Assert.Equal(1, _queue.CurrentIndex);
    }

    [Fact]
    public async Task DoubleNext_WhileLoading_SkipsTwoTracks()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2", "t3"));
        await WaitForPreloadAsync(); // settle the detached preload before poisoning t2

        var gate = _streams.Block("t2");
        var first = coordinator.NextAsync();   // advances to t2, blocks resolving it
        var second = coordinator.NextAsync();  // supersedes the first mid-resolve
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal("t3", coordinator.NowPlaying!.VideoId);
        Assert.Equal(2, _queue.CurrentIndex);
    }

    [Fact]
    public async Task FaultRecovery_SupersededByNext_IsAbandoned()
    {
        var notifications = new List<PlaybackNotice>();
        using var coordinator = CreateCoordinator();
        coordinator.Notification += notifications.Add;
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));

        var gate = _streams.Block("t1"); // stalls the recovery re-resolve
        _player.Raise(new PlayerFaultedEvent(_player.Current!.Key, "URL expired"));
        await TestTracks.WaitUntilAsync(() => _streams.Calls.Contains(("t1", true)));

        var next = coordinator.NextAsync();
        gate.SetResult();
        await next;

        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
        Assert.StartsWith("t2#", _player.Current!.Key);
        Assert.DoesNotContain(notifications, n => n.Kind == PlaybackNoticeKind.TrackSkipped);
    }

    [Fact]
    public async Task Seek_PreemptsInFlightPreload_AndRearmsAfter()
    {
        var gate = _streams.Block("t2"); // preload of t2 stalls holding the gate
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));
        await TestTracks.WaitUntilAsync(() => _streams.Calls.Contains(("t2", false)));

        // Without preemption this waits on the gate until the preload finishes.
        await coordinator.SeekAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("seek:30", _player.Log);
        Assert.Null(_player.Next);

        // The preempted preload re-arms once resolution can proceed.
        gate.SetResult();
        await WaitForPreloadAsync();
        Assert.StartsWith("t2#", _player.Next!.Key);
    }

    [Fact]
    public async Task Stop_ClearsStateAndStopsPlayer()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1"));

        coordinator.Stop();

        await TestTracks.WaitUntilAsync(() => coordinator.State == PlaybackState.Stopped);
        Assert.Null(coordinator.NowPlaying);
        Assert.Contains("stop", _player.Log);
    }

    [Fact]
    public async Task Starvation_WhilePaused_StaysPaused()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1"));
        coordinator.Pause();

        _player.RaiseProgress(TimeSpan.FromSeconds(5), starved: true);

        Assert.Equal(PlaybackState.Paused, coordinator.State);
    }

    [Fact]
    public void Volume_IsClamped()
    {
        using var coordinator = CreateCoordinator();

        coordinator.Volume = 1.5;
        Assert.Equal(1.0, _player.Volume);
        Assert.Equal(1.0, _settings.Volume);

        coordinator.Volume = -0.1;
        Assert.Equal(0.0, _player.Volume);
    }

    [Fact]
    public void VolumeChanged_ReportsEachChangeOnce()
    {
        _settings.Volume = 0.5;
        using var coordinator = CreateCoordinator();
        var reported = new List<double>();
        coordinator.VolumeChanged += reported.Add;

        coordinator.Volume = 0.8;
        coordinator.Volume = 0.8;  // the same again: nothing changed
        coordinator.Volume = 2.0;  // clamped to 1

        Assert.Equal([0.8, 1.0], reported);
        Assert.Equal(1.0, _settings.Volume);
    }

    [Fact]
    public async Task DeviceLost_PausesAndNotifies()
    {
        using var coordinator = CreateCoordinator();
        var notifications = new List<PlaybackNotice>();
        coordinator.Notification += notifications.Add;
        await coordinator.PlayQueueAsync(Tracks("t1"));

        _player.Raise(new DeviceLostEvent());

        Assert.Equal(PlaybackState.Paused, coordinator.State);
        Assert.Contains(notifications, n => n.Kind == PlaybackNoticeKind.OutputLost);
    }

    [Fact]
    public async Task DeviceChanged_NotifiesWithoutTouchingPlayback()
    {
        using var coordinator = CreateCoordinator();
        var notifications = new List<PlaybackNotice>();
        coordinator.Notification += notifications.Add;
        await coordinator.PlayQueueAsync(Tracks("t1"));

        _player.Raise(new DeviceChangedEvent("Speakers (USB DAC)"));

        Assert.Equal(PlaybackState.Playing, coordinator.State);
        Assert.Contains(new PlaybackNotice(PlaybackNoticeKind.OutputChanged, "Speakers (USB DAC)"), notifications);
    }

    [Fact]
    public async Task Loudness_FlowsIntoRequests_WhenNormalizing()
    {
        _streams.LoudnessDb["t1"] = 4.4;
        _streams.LoudnessDb["t2"] = -2.1;
        using var coordinator = CreateCoordinator(); // NormalizeVolume defaults on

        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));

        Assert.Equal(4.4, _player.Current!.LoudnessDb);
        await WaitForPreloadAsync();
        Assert.Equal(-2.1, _player.Next!.LoudnessDb);
    }

    [Fact]
    public async Task Loudness_Stripped_WhenNormalizationOff()
    {
        _streams.LoudnessDb["t1"] = 4.4;
        _settings.NormalizeVolume = false;
        using var coordinator = CreateCoordinator();

        await coordinator.PlayQueueAsync(Tracks("t1"));

        Assert.Null(_player.Current!.LoudnessDb);
    }

    [Fact]
    public async Task SkipFade_FadesIntoArmedNext_AdvancingQueueWithoutReplay()
    {
        _settings.CrossfadeEnabled = true; // CrossfadeOnSkip defaults true
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2", "t3"));
        await WaitForPreloadAsync();

        await coordinator.NextAsync();

        // Capped at 2 s despite the 6 s crossfade setting; the armed track is
        // not replayed from scratch, and the queue advanced immediately.
        Assert.Contains("crossfade:2", _player.Log);
        Assert.DoesNotContain(_player.Log, entry => entry.StartsWith("play:t2#"));
        Assert.Equal(1, _queue.CurrentIndex);
        // NowPlaying flips when the engine actually promotes, not before.
        Assert.Equal("t1", coordinator.NowPlaying!.VideoId);
    }

    [Fact]
    public async Task SkipFade_Promotion_DoesNotAdvanceQueueAgain()
    {
        _settings.CrossfadeEnabled = true;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2", "t3"));
        await WaitForPreloadAsync();
        await coordinator.NextAsync();

        var promotedKey = _player.Next!.Key;
        _player.Current = _player.Next; // fade completed inside the mixer
        _player.Raise(new TrackPromotedEvent(promotedKey));

        await TestTracks.WaitUntilAsync(() => coordinator.NowPlaying?.VideoId == "t2");
        Assert.Equal(1, _queue.CurrentIndex);
        await TestTracks.WaitUntilAsync(() => _player.Next?.Key.StartsWith("t3#") == true);
    }

    [Fact]
    public async Task SkipFade_Disabled_CutsToNext()
    {
        _settings.CrossfadeEnabled = true;
        _settings.CrossfadeOnSkip = false;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));
        await WaitForPreloadAsync();

        await coordinator.NextAsync();

        Assert.DoesNotContain(_player.Log, entry => entry.StartsWith("crossfade"));
        Assert.Contains(_player.Log, entry => entry.StartsWith("play:t2#"));
        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
    }

    [Fact]
    public async Task Seek_DuringCrossfade_RearmsTheNextTrack_AndFadesAgainAtTheNewEnd()
    {
        _settings.CrossfadeEnabled = true;
        _settings.CrossfadeSeconds = 8;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));
        await WaitForPreloadAsync();
        _player.RaiseProgress(TimeSpan.FromSeconds(93)); // the fade into t2 begins
        var fadingInto = _player.Next!.Key;

        await coordinator.SeekAsync(TimeSpan.FromSeconds(10));

        // The fade had started reading t2, so it is disarmed before the seek and
        // armed again from its first sample afterwards.
        Assert.Null(_player.NextAtSeek);
        await TestTracks.WaitUntilAsync(() => _player.Next?.Key.StartsWith("t2#") == true && _player.Next.Key != fadingInto);
        Assert.Equal("t1", coordinator.NowPlaying!.VideoId);

        _player.RaiseProgress(TimeSpan.FromSeconds(95));
        Assert.Equal(2, _player.Log.Count(entry => entry.StartsWith("crossfade")));
        Assert.Contains("crossfade:5", _player.Log);
    }

    [Fact]
    public async Task Seek_WithNoFadeRunning_KeepsTheNextTrackArmed()
    {
        _settings.CrossfadeEnabled = true;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));
        await WaitForPreloadAsync();
        var armed = _player.Next!.Key;

        await coordinator.SeekAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(armed, _player.NextAtSeek?.Key);
        Assert.Equal(armed, _player.Next?.Key);
        Assert.Equal(1, _player.Log.Count(entry => entry.StartsWith("preload:")));
    }

    [Fact]
    public async Task Seek_ThatDropsTheNextTrack_ArmsItAgain_AndFadesAtTheNewEnd()
    {
        // A fade the coordinator had no mark for: the seek's cut drops the
        // part-read t2, and the coordinator learns it only from the seek's result.
        _settings.CrossfadeEnabled = true;
        _settings.CrossfadeSeconds = 8;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));
        await WaitForPreloadAsync();
        var dropped = _player.Next!.Key;
        _player.DropNextOnSeek = true;

        await coordinator.SeekAsync(TimeSpan.FromSeconds(10));

        await TestTracks.WaitUntilAsync(() => _player.Next?.Key.StartsWith("t2#") == true && _player.Next.Key != dropped);
        _player.RaiseProgress(TimeSpan.FromSeconds(95));
        Assert.Contains("crossfade:5", _player.Log);
    }

    [Fact]
    public async Task Progress_ArrivingDuringASeek_DoesNotBeginAFade()
    {
        _settings.CrossfadeEnabled = true;
        _settings.CrossfadeSeconds = 8;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));
        await WaitForPreloadAsync();

        // The engine keeps reporting the old position while the seek prebuffers.
        _player.DuringSeek = () => _player.RaiseProgress(TimeSpan.FromSeconds(95));
        await coordinator.SeekAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(_player.Log, entry => entry.StartsWith("crossfade"));
    }

    [Fact]
    public async Task Progress_FromTheChainASeekReplaced_ArrivingAfterIt_IsIgnored()
    {
        // A tick read from the old chain just before the swap reaches the
        // coordinator after the seek has landed.
        _settings.CrossfadeEnabled = true;
        _settings.CrossfadeSeconds = 8;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));
        await WaitForPreloadAsync();
        var oldChain = _player.Chain;
        var reported = new List<TimeSpan>();
        coordinator.ProgressChanged += progress => reported.Add(progress.Position);

        await coordinator.SeekAsync(TimeSpan.FromSeconds(10));
        _player.RaiseProgress(TimeSpan.FromSeconds(95), chain: oldChain);

        Assert.DoesNotContain(_player.Log, entry => entry.StartsWith("crossfade"));
        Assert.Equal(TimeSpan.FromSeconds(10), reported[^1]);

        // The new chain's ticks still count: its own end fades.
        _player.RaiseProgress(TimeSpan.FromSeconds(95));
        Assert.Contains("crossfade:5", _player.Log);
    }

    [Fact]
    public async Task Progress_FromATrackPromotedDuringASeek_StillCounts()
    {
        // The seek's chain belongs to the track it seeked; the promoted track's
        // ticks come from a chain of its own and must not be filtered by it.
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2", "t3"));
        await WaitForPreloadAsync();
        var promotedKey = _player.Next!.Key;
        _player.DuringSeek = () =>
        {
            _player.Current = _player.Next;
            _player.Raise(new TrackPromotedEvent(promotedKey));
        };
        var reported = new List<TimeSpan>();
        coordinator.ProgressChanged += progress => reported.Add(progress.Position);

        await coordinator.SeekAsync(TimeSpan.FromSeconds(10));
        _player.RaiseProgress(TimeSpan.FromSeconds(42), chain: -1);

        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
        Assert.Equal(TimeSpan.FromSeconds(42), reported[^1]);
    }

    [Fact]
    public async Task Seek_DuringSkipFade_IsDropped()
    {
        // The skip already moved the queue to t2; seeking t1 would play the track
        // the user just left, with the queue pointing past it.
        _settings.CrossfadeEnabled = true;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2", "t3"));
        await WaitForPreloadAsync();
        await coordinator.NextAsync();
        Assert.Contains("crossfade:2", _player.Log);

        await coordinator.SeekAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(_player.Log, entry => entry.StartsWith("seek:"));
        Assert.StartsWith("t2#", _player.Next!.Key);
        Assert.Equal(1, _queue.CurrentIndex);
    }

    [Fact]
    public async Task Seek_WhileTheFadesPromotionIsInFlight_StillPromotes()
    {
        _settings.CrossfadeEnabled = true;
        _settings.CrossfadeSeconds = 8;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2", "t3"));
        await WaitForPreloadAsync();
        _player.RaiseProgress(TimeSpan.FromSeconds(93));
        var fadingInto = _player.Next!.Key;

        // The mixer finished the fade; its promotion event hops threads and
        // arrives after the seek has disarmed the successor.
        _player.Current = _player.Next;
        await coordinator.SeekAsync(TimeSpan.FromSeconds(10));
        _player.Raise(new TrackPromotedEvent(fadingInto));

        await TestTracks.WaitUntilAsync(() => coordinator.NowPlaying?.VideoId == "t2");
        Assert.Equal(1, _queue.CurrentIndex);
        // Not t2 again (re-armed meanwhile as t1's successor): t3 follows.
        await TestTracks.WaitUntilAsync(() => _player.Next?.Key.StartsWith("t3#") == true);
    }

    [Fact]
    public async Task Previous_DuringSkipFade_GoesBackToTheTrackBeingLeft()
    {
        _settings.CrossfadeEnabled = true;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2", "t3"));
        await WaitForPreloadAsync();
        _player.RaiseProgress(TimeSpan.FromSeconds(50)); // past the restart threshold
        await coordinator.NextAsync();

        await coordinator.PreviousAsync();

        await TestTracks.WaitUntilAsync(() => _player.Log.Any(entry => entry.StartsWith("play:t1#") && entry.EndsWith("@0")));
        Assert.Equal(0, _queue.CurrentIndex);
        Assert.Equal("t1", coordinator.NowPlaying!.VideoId);
    }

    [Fact]
    public async Task SkipFade_RepeatOne_CutsBecauseArmedTrackIsNotManualNext()
    {
        _settings.CrossfadeEnabled = true;
        _queue.Repeat = RepeatMode.One;
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"));
        await WaitForPreloadAsync(); // repeat-one arms t1 again (natural end repeats it)

        await coordinator.NextAsync();

        // A manual skip must land on t2; the armed repeat of t1 can't be faded into.
        Assert.DoesNotContain(_player.Log, entry => entry.StartsWith("crossfade"));
        await TestTracks.WaitUntilAsync(() => coordinator.NowPlaying?.VideoId == "t2");
    }

    [Fact]
    public async Task ExtendFromSeed_ArmsTheSuccessor_ForAOneTrackQueue()
    {
        using var coordinator = CreateCoordinator();
        var seed = TestTracks.Track("seed");
        await coordinator.PlayQueueAsync([seed]);
        Assert.Null(_player.Next); // nothing to preload yet

        var added = coordinator.ExtendFromSeed(seed, Tracks("seed", "r1", "r2"));

        Assert.Equal(2, added);
        await WaitForPreloadAsync();
        Assert.StartsWith("r1#", _player.Next!.Key);
    }

    [Fact]
    public async Task TogglePlayPause_WhenStoppedWithAQueue_PlaysTheCurrentTrack()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.PlayQueueAsync(Tracks("t1", "t2"), startIndex: 1);
        coordinator.Stop();
        await TestTracks.WaitUntilAsync(() => coordinator.State == PlaybackState.Stopped);

        coordinator.TogglePlayPause();

        await TestTracks.WaitUntilAsync(() => coordinator.State == PlaybackState.Playing);
        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
    }

    [Fact]
    public void TogglePlayPause_WhenStoppedWithNothingQueued_DoesNothing()
    {
        using var coordinator = CreateCoordinator();

        coordinator.TogglePlayPause();

        Assert.Equal(PlaybackState.Stopped, coordinator.State);
        Assert.Empty(_player.Log);
    }
}
