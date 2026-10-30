using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;
using Rambutan.Core.Settings;

namespace Rambutan.Core.Playback;

/// <summary>
/// Drives playback: queue advance → resolve → prepare → play, gapless preload of
/// the successor, position-driven crossfade scheduling, starvation → Buffering
/// mapping, and fault recovery (re-resolve once, then skip forward; when the
/// network is gone, wait for it and pick the track up where it stopped).
///
/// Concurrency model: user commands and engine events race, so every async
/// sequence that touches the player runs under a single gate (<see cref="_gate"/>),
/// FIFO. Each command bumps <see cref="_commandVersion"/> at issue time and cancels
/// whatever is executing; a command whose version is stale by the time it runs
/// still applies its queue mutation (so two rapid Next presses skip two tracks)
/// but leaves the expensive resolve/play realization to the newest command.
/// Engine reactions (ended/faulted) validate their triggering key AND the version
/// captured at fire time once they own the gate, so a reaction that lost a race
/// against a user command drops out instead of double-advancing the queue.
/// Promotion is applied synchronously (the engine already switched tracks;
/// the coordinator just mirrors reality).
///
/// All state-machine transitions happen while holding <see cref="_sync"/>, making
/// check-and-transition atomic. StateChanged/NowPlayingChanged may therefore fire
/// while the lock is held and on non-UI threads — subscribers must marshal
/// asynchronously (Post, not blocking Invoke) and must not call back in synchronously.
/// </summary>
public sealed class PlaybackCoordinator : IDisposable
{
    private const int MaxConsecutivePlayFailures = 3;
    private static readonly TimeSpan PreviousRestartsThreshold = TimeSpan.FromSeconds(3);

    /// <summary>A skip fade must feel responsive regardless of the configured
    /// end-of-track crossfade length, so it is capped short.</summary>
    private static readonly TimeSpan MaxSkipFade = TimeSpan.FromSeconds(2);

    /// <summary>How far into a queue's last track autoplay fetches what follows it:
    /// early enough to arm the first autoplay track gaplessly, late enough
    /// that a shelf song's own radio (which arrives within a second or two) and
    /// quick skips through a queue's end don't each cost a radio request.</summary>
    private static readonly TimeSpan AutoplayLead = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait between tries while a track waits for the network:
    /// quick at first, for a brief drop, then every 15 seconds.</summary>
    public static readonly IReadOnlyList<TimeSpan> NetworkRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)];

    /// <summary>How long a track waits for the network before playback moves to
    /// what plays offline, or stops.</summary>
    public static readonly TimeSpan NetworkWaitLimit = TimeSpan.FromMinutes(10);

    private readonly IStreamSource _streams;
    private readonly IAudioPlayer _player;
    private readonly QueueModel _queue;
    private readonly PlaybackSettings _settings;
    private readonly IRadioSource? _radio;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // Autoplay: videoIds played this session, so a continuation prefers new
    // songs; the one radio fetch in flight or done for the current seed, shared by
    // the fetch-ahead and the queue's actual end; and the track key fetch-ahead
    // last fired for, so it fires once per track.
    private readonly HashSet<string> _playedThisSession = [];
    private (TrackInfo Seed, Task<IReadOnlyList<TrackInfo>> Fetch)? _autoplayFetch;
    private string? _autoplayAheadKey;
    private readonly PlayerStateMachine _machine = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private long _keyCounter;
    private int _commandVersion;
    private CancellationTokenSource? _activeWork;     // executing command or reaction
    private CancellationTokenSource? _activePreload;  // detached (post-promotion) preload

    private string? _currentKey;
    private TrackInfo? _currentTrack;
    private TimeSpan _position;
    private TimeSpan? _duration;

    private string? _nextKey;
    private TrackInfo? _nextTrack;

    private bool _crossfadeStarted;
    private bool _recoveryAttempted;

    /// <summary>The track a seek last landed on, and the decode chain it put that
    /// track on: progress from any other chain of the same key is stale.</summary>
    private (string Key, long Chain)? _seekedChain;

    /// <summary>The current track lost the network mid-play and waits for it
    /// (the fault reaction holds the gate meanwhile).</summary>
    private bool _waitingForNetwork;

    private TrackInfo? _announcedWait;

    /// <summary>A seek made while waiting for the network: where the track
    /// resumes, instead of where it stopped.</summary>
    private TimeSpan? _seekWhileWaiting;

    /// <summary>A skip fade already advanced the queue; the promotion it leads to
    /// must not advance again.</summary>
    private bool _skipAdvancedQueue;

    /// <summary>An engine-event reaction holds the gate: a track it starts
    /// started on its own, not at the user's command.</summary>
    private bool _reacting;

    /// <summary>The successor a seek disarmed mid-fade. The engine may already
    /// have promoted it, with the event still on its way; that promotion must
    /// still count. Keys are unique, so a stale entry never matches anything else.</summary>
    private string? _disarmedKey;
    private TrackInfo? _disarmedTrack;

    public PlaybackCoordinator(
        IStreamSource streams, IAudioPlayer player, QueueModel queue, PlaybackSettings settings,
        IRadioSource? radio = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _radio = radio;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        _streams = streams;
        _player = player;
        _queue = queue;
        _settings = settings;
        _player.Volume = Math.Clamp(settings.Volume, 0.0, 1.0);
        _player.EventRaised += OnPlayerEvent;
        _machine.StateChanged += s => StateChanged?.Invoke(s);
    }

    public PlaybackState State => _machine.State;
    public QueueModel Queue => _queue;

    public TrackInfo? NowPlaying
    {
        get { lock (_sync) return _currentTrack; }
    }

    public double Volume
    {
        get => _player.Volume;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (clamped == _player.Volume)
                return;
            _player.Volume = clamped;
            _settings.Volume = clamped;
            VolumeChanged?.Invoke(clamped);
        }
    }

    /// <summary>Raised when <see cref="Volume"/> changes, from whichever surface set
    /// it (the player bar, a shortcut, the phone remote), on the setter's thread.</summary>
    public event Action<double>? VolumeChanged;

    /// <summary>
    /// The YouTube Music id whose radio autoplay continues a track that isn't on
    /// YouTube (a local file) with: its match, waited for when a search is under
    /// way. Null, or a null answer, stops autoplay with a notice; a throw is a
    /// failed autoplay. Unset, such a track always stops it.
    /// </summary>
    public Func<TrackInfo, CancellationToken, Task<string?>>? RadioSeedFor { get; set; }

    public event Action<PlaybackState>? StateChanged;
    public event Action<TrackInfo?>? NowPlayingChanged;

    /// <summary>A track began to play: raised with each non-null
    /// <see cref="NowPlayingChanged"/>, saying whether the user started it or it
    /// started on its own (the last one ended, autoplay, a skip past a failure).</summary>
    public event Action<TrackStart>? TrackStarted;
    public event Action<PlaybackProgress>? ProgressChanged;

    /// <summary>The track that lost the network mid-play and waits for it to come
    /// back, or null. Standing state: it lasts as long as the wait, which can be
    /// minutes, so the app shows it as a state rather than a notice.</summary>
    public TrackInfo? TrackWaitingForNetwork
    {
        get { lock (_sync) return _announcedWait; }
    }

    public event Action<TrackInfo?>? NetworkWaitChanged;
    /// <summary>User-facing, non-modal messages ("Couldn't play X, skipping"),
    /// as codes the app words.</summary>
    public event Action<PlaybackNotice>? Notification;

    public Task PlayQueueAsync(
        IReadOnlyList<TrackInfo> tracks, int startIndex = 0, CancellationToken ct = default) =>
        RunCommandAsync(async (seq, token) =>
        {
            _queue.SetQueue(tracks, startIndex);
            if (!IsSuperseded(seq))
                await PlayCurrentAsync(TimeSpan.Zero, seq, token);
        }, ct);

    /// <summary>
    /// Puts <paramref name="tracks"/> right after the current track, in their
    /// order, and plays the first of them; the rest of the queue stays. With no
    /// current track the first becomes it.
    /// </summary>
    public Task PlayAfterCurrentAsync(IReadOnlyList<TrackInfo> tracks, CancellationToken ct = default) =>
        RunCommandAsync(async (seq, token) =>
        {
            if (tracks.Count == 0)
                return;
            var current = _queue.CurrentIndex;
            _queue.EnqueueNextRange(tracks);
            if (_queue.JumpTo(current < 0 ? 0 : current + 1) is not null && !IsSuperseded(seq))
                await PlayCurrentAsync(TimeSpan.Zero, seq, token);
        }, ct);

    /// <summary>Plays the queue's track at <paramref name="queueIndex"/>, from
    /// <paramref name="startAt"/> (a lyric line picked before anything was loaded).</summary>
    public Task JumpToAsync(int queueIndex, TimeSpan startAt = default, CancellationToken ct = default) =>
        RunCommandAsync(async (seq, token) =>
        {
            if (_queue.JumpTo(queueIndex) is not null && !IsSuperseded(seq))
                await PlayCurrentAsync(startAt, seq, token);
        }, ct);

    public Task NextAsync(CancellationToken ct = default) =>
        RunCommandAsync(async (seq, token) =>
        {
            // Fade-into-next: reuses the armed successor and
            // the promotion path; only the newest command may start one.
            if (!IsSuperseded(seq) && TryBeginSkipFade())
                return;

            if (_queue.NextTrack(TrackEndReason.Manual) is not null)
            {
                if (!IsSuperseded(seq))
                    await PlayCurrentAsync(TimeSpan.Zero, seq, token);
            }
            // Next on the last track: autoplay continues from it, as when it ends.
            // The track keeps playing while the radio is fetched, and keeps
            // playing if nothing comes back.
            else if (!IsSuperseded(seq) && State is not PlaybackState.Stopped &&
                     await TryContinueWithAutoplayAsync(TrackEndReason.Manual, token) &&
                     !IsSuperseded(seq))
            {
                await PlayCurrentAsync(TimeSpan.Zero, seq, token);
            }
        }, ct);

    public Task PreviousAsync(CancellationToken ct = default)
    {
        // Restarting a track that waits for the network is a seek, not a command:
        // a command would end the wait and leave the track dead.
        lock (_sync)
        {
            if (_waitingForNetwork && _position > PreviousRestartsThreshold)
            {
                SeekWhileWaiting(TimeSpan.Zero);
                return Task.CompletedTask;
            }
        }

        return RunCommandAsync(async (seq, token) =>
        {
            TimeSpan position;
            bool skipping;
            lock (_sync)
            {
                position = _position;
                // Mid skip fade the queue has already moved on, so Previous goes
                // back to the track being left rather than seeking it.
                skipping = _skipAdvancedQueue;
            }

            if ((position > PreviousRestartsThreshold && !skipping) || _queue.PreviousTrack() is null)
            {
                if (State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering)
                    await SeekCoreAsync(TimeSpan.Zero, token);
                return;
            }

            if (!IsSuperseded(seq))
                await PlayCurrentAsync(TimeSpan.Zero, seq, token);
        }, ct);
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_machine.State is not (PlaybackState.Playing or PlaybackState.Buffering))
                return;
            _machine.TransitionTo(PlaybackState.Paused);
            _player.Pause(); // non-blocking flag flip; inside the lock so pause/resume can't invert
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (_machine.State is not PlaybackState.Paused)
                return;
            _machine.TransitionTo(PlaybackState.Playing);
            _player.Resume();
        }
    }

    /// <summary>
    /// Play/pause as every surface means it: the player bar, the tray and media keys.
    /// Stopped with tracks still queued (the queue ran out, or playback gave up
    /// after repeated failures) starts the queue's current track again; Pause does
    /// nothing when stopped.
    /// </summary>
    public void TogglePlayPause()
    {
        switch (State)
        {
            case PlaybackState.Paused:
                Resume();
                break;
            case PlaybackState.Stopped when _queue.CurrentIndex >= 0:
                _ = JumpToAsync(_queue.CurrentIndex);
                break;
            default:
                Pause();
                break;
        }
    }

    /// <summary>
    /// Seeks the current track. Serialized with commands (never supersedes them),
    /// but preempts opportunistic preload — whose resolve + prebuffer can hold the
    /// gate for seconds — and re-arms it afterwards if it never committed.
    /// </summary>
    public async Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        lock (_sync)
        {
            // The gate is held until the network is back.
            if (_waitingForNetwork)
            {
                SeekWhileWaiting(position);
                return;
            }
            _activePreload?.Cancel();
        }

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await SeekCoreAsync(position, ct);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _gate.Release();
        }

        RearmPreloadIfIdle();
    }

    /// <summary>
    /// Extends a queue that <paramref name="seed"/> started with the tracks that follow
    /// it (see <see cref="QueueModel.ExtendFromSeed"/>), then arms the successor. A
    /// one-track queue had nothing to preload when it began, so without the re-arm the
    /// first transition into the new tracks would not be gapless.
    /// </summary>
    /// <returns>How many tracks were added.</returns>
    public int ExtendFromSeed(TrackInfo seed, IReadOnlyList<TrackInfo> tracks)
    {
        var added = _queue.ExtendFromSeed(seed, tracks);
        if (added > 0)
            RearmPreloadIfIdle();
        return added;
    }

    private void RearmPreloadIfIdle()
    {
        string? currentKey;
        int seq;
        lock (_sync)
        {
            currentKey = _nextKey is null ? _currentKey : null;
            seq = _commandVersion;
        }
        if (currentKey is not null)
            _ = PreloadDetachedAsync(currentKey, seq);
    }

    public void Stop() =>
        _ = RunCommandAsync((seq, _) =>
        {
            if (!IsSuperseded(seq))
                StopCore();
            return Task.CompletedTask;
        }, CancellationToken.None);

    /// <summary>
    /// Attempts to realize a manual skip as a fade into the armed successor
    /// (gate held). Advances the queue immediately — two rapid presses must
    /// still skip two tracks — and flags the pending promotion so it doesn't
    /// advance again. Returns false (caller cuts) when disabled, nothing is
    /// armed, a fade is already running, or the armed track isn't the manual
    /// successor (repeat-one arms the current track again).
    /// </summary>
    private bool TryBeginSkipFade()
    {
        TimeSpan fade;
        lock (_sync)
        {
            if (!_settings.CrossfadeEnabled || !_settings.CrossfadeOnSkip)
                return false;
            if (_nextKey is null || _nextTrack is null || _crossfadeStarted)
                return false;
            if (_machine.State is not (PlaybackState.Playing or PlaybackState.Buffering))
                return false;
            if (!ReferenceEquals(_queue.PeekNext(TrackEndReason.Manual), _nextTrack))
                return false;

            _queue.NextTrack(TrackEndReason.Manual);
            _skipAdvancedQueue = true;
            _crossfadeStarted = true;
            fade = TimeSpan.FromSeconds(Math.Min(
                _settings.CrossfadeSeconds, MaxSkipFade.TotalSeconds));
        }

        _player.BeginCrossfade(fade);
        return true;
    }

    /// <summary>
    /// Runs a user command: bumps the version (superseding in-flight realizations),
    /// cancels the currently executing work, and runs <paramref name="work"/> under
    /// the gate. The command's own queue mutation always applies; it checks
    /// <see cref="IsSuperseded"/> itself before the expensive realization.
    /// </summary>
    private async Task RunCommandAsync(Func<int, CancellationToken, Task> work, CancellationToken external)
    {
        int seq;
        lock (_sync)
        {
            seq = ++_commandVersion;
            _activeWork?.Cancel();
            _activePreload?.Cancel();
        }

        await _gate.WaitAsync(CancellationToken.None);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        try
        {
            if (external.IsCancellationRequested)
                return;
            lock (_sync) _activeWork = cts;
            await work(seq, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer command (or externally cancelled); the newest
            // command owns the player from here.
        }
        catch (Exception ex)
        {
            Notification?.Invoke(new(PlaybackNoticeKind.CommandFailed, Detail: ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs an engine-event reaction. Validates under the gate that the triggering
    /// key is still current AND that no command was issued since the event fired —
    /// a reaction that lost that race must not advance the queue a second time.
    /// </summary>
    private async Task RunReactionAsync(string triggerKey, Func<int, CancellationToken, Task> work)
    {
        int seqAtFire;
        lock (_sync)
        {
            seqAtFire = _commandVersion;
            // A reaction may preempt an in-flight preload (never a user command):
            // whatever it was arming is either replayed from scratch or moot now.
            _activePreload?.Cancel();
        }

        await _gate.WaitAsync(CancellationToken.None);
        var cts = new CancellationTokenSource();
        try
        {
            lock (_sync)
            {
                if (triggerKey != _currentKey || seqAtFire != _commandVersion)
                    return;
                _activeWork = cts;
                _reacting = true;
            }
            await work(seqAtFire, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Notification?.Invoke(new(PlaybackNoticeKind.CommandFailed, Detail: ex.Message));
        }
        finally
        {
            lock (_sync) _reacting = false;
            _gate.Release();
        }
    }

    private bool IsSuperseded(int seq)
    {
        lock (_sync) return seq != _commandVersion;
    }

    private async Task PlayCurrentAsync(TimeSpan startAt, int seq, CancellationToken ct)
    {
        for (var failures = 0; failures < MaxConsecutivePlayFailures; failures++)
        {
            var track = _queue.Current;
            if (track is null)
            {
                StopCore();
                return;
            }

            try
            {
                await PlayTrackAsync(track, startAt, seq, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OfflineException ex)
            {
                // Trying the tracks in between would fail the same way, one
                // network timeout at a time; go straight to one that plays offline.
                AppLog.Write("playback", $"{track.VideoId} needs the network: {ex.Message}");
                if (NextOfflineIndex() is not { } index)
                {
                    Notification?.Invoke(new(PlaybackNoticeKind.StoppedOffline, track.Title));
                    StopCore();
                    return;
                }

                Notification?.Invoke(new(PlaybackNoticeKind.SkippedOffline, track.Title));
                _queue.JumpTo(index);
                startAt = TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                Notification?.Invoke(new(PlaybackNoticeKind.TrackFailed, track.Title, ex.Message));
                if (_queue.NextTrack(TrackEndReason.Manual) is null)
                {
                    StopCore();
                    return;
                }
                startAt = TimeSpan.Zero;
            }
        }

        Notification?.Invoke(new(PlaybackNoticeKind.StoppedAfterFailures));
        StopCore();
    }

    /// <summary>The queue index of the first track after the current one that the
    /// source plays offline, in play order (wrapping round under Repeat All), or null
    /// when there is none or the source can't tell.</summary>
    private int? NextOfflineIndex()
    {
        if (_streams is not IOfflineAvailability offline)
            return null;

        var items = _queue.Items;
        var current = _queue.CurrentIndex;
        if (current < 0 || current >= items.Count)
            return null;

        var steps = _queue.Repeat == RepeatMode.All ? items.Count - 1 : items.Count - 1 - current;
        for (var step = 1; step <= steps; step++)
        {
            var index = (current + step) % items.Count;
            var item = items[index];
            if (offline.IsAvailableOffline(item.VideoId)
                || (AlbumAudio.Fallback(item) is { } listed && offline.IsAvailableOffline(listed.VideoId)))
                return index;
        }

        return null;
    }

    private async Task PlayTrackAsync(TrackInfo track, TimeSpan startAt, int seq, CancellationToken ct)
    {
        lock (_sync)
        {
            _nextKey = null;
            _nextTrack = null;
            _crossfadeStarted = false;
            _skipAdvancedQueue = false;
            _recoveryAttempted = false;
            _machine.TransitionTo(PlaybackState.Loading);
        }
        _player.ClearNext();

        string key;
        try
        {
            key = await StartAsync(track, startAt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && AlbumAudio.Fallback(track) is { } listed)
        {
            // An album row's audio track that won't play: the video the page
            // listed is the same song, as YouTube Music pairs them. It takes
            // the row's place, so the queue, lyrics and downloads follow it.
            AppLog.Write("playback", $"album audio {track.VideoId} won't play ({ex.Message}); playing the listed video {listed.VideoId}");
            _queue.ReplaceCurrent(track, listed);
            track = listed;
            key = await StartAsync(track, startAt, ct);
        }

        // Commit unconditionally, even if superseded mid-play: the engine really is
        // playing this track now, and the superseding command overwrites right after.
        lock (_sync)
        {
            _currentKey = key;
            _currentTrack = track;
            _position = startAt;
            _duration = track.Duration;
            _playedThisSession.Add(track.VideoId);
            _machine.TransitionTo(PlaybackState.Playing);
            NowPlayingChanged?.Invoke(track);
            TrackStarted?.Invoke(new(track, OnItsOwn: _reacting));
        }

        // Detached, not awaited: preload's resolve + prebuffer must not extend the
        // command's hold on the gate, or a seek right after play-start stalls for
        // seconds behind it. It queues on the gate and runs once we release.
        _ = PreloadDetachedAsync(key, seq);
    }

    /// <summary>Resolves the track and starts it in the engine; returns its key.</summary>
    private async Task<string> StartAsync(TrackInfo track, TimeSpan startAt, CancellationToken ct)
    {
        var media = await ResolveWithRetryAsync(track.VideoId, ct);
        var key = NewKey(track);
        try
        {
            await _player.PlayAsync(
                new AudioTrackRequest(
                    key, media.Url, track.Title, track.Duration, startAt, Loudness(media)), ct);
        }
        catch (StreamOpenException ex)
        {
            // The URL resolved but wouldn't open — a cached googlevideo URL gone
            // stale (IP-bound, can die before nominal expiry) or a corrupt cached
            // file. Mint a fresh resolution and try once more before skipping.
            AppLog.Write("playback", $"open of {track.VideoId} failed ({ex.Message}); retrying with fresh URL");
            media = await _streams.ResolveAsync(track.VideoId, bypassCache: true, ct);
            key = NewKey(track);
            await _player.PlayAsync(
                new AudioTrackRequest(
                    key, media.Url, track.Title, track.Duration, startAt, Loudness(media)), ct);
        }
        return key;
    }

    private async Task<ResolvedMedia> ResolveWithRetryAsync(string videoId, CancellationToken ct)
    {
        try
        {
            return await _streams.ResolveAsync(videoId, bypassCache: false, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OfflineException)
        {
            AppLog.Write("playback", $"resolve of {videoId} failed ({ex.Message}); retrying with bypass");
            return await _streams.ResolveAsync(videoId, bypassCache: true, ct);
        }
    }

    /// <summary>
    /// Resolves and arms the gapless/crossfade successor. Gate held by the caller.
    /// Commits only if <paramref name="parentKey"/> is still the current track and no
    /// command was issued since; the arm is undone otherwise. Failures are swallowed —
    /// preload is opportunistic and the Ended path plays the next track from scratch.
    /// </summary>
    private async Task PreloadNextAsync(string parentKey, int parentSeq, CancellationToken ct)
    {
        try
        {
            lock (_sync)
            {
                // _nextKey set means an earlier arm already committed — a re-arm
                // (e.g. post-seek) would redundantly resolve and prebuffer again.
                if (_currentKey != parentKey || _commandVersion != parentSeq || _nextKey is not null)
                    return;
            }

            var next = _queue.PeekNext(TrackEndReason.Natural);
            if (next is null)
                return;

            var media = await _streams.ResolveAsync(next.VideoId, bypassCache: false, ct);
            if (Superseded())
                return;

            var key = NewKey(next);
            Task Arm(ResolvedMedia resolved) =>
                _player.PreloadNextAsync(
                    new AudioTrackRequest(
                        key, resolved.Url, next.Title, next.Duration, TimeSpan.Zero, Loudness(resolved)), ct);

            try
            {
                await Arm(media);
            }
            catch (StreamOpenException ex)
            {
                // A URL googlevideo serves only the first megabyte of, or one
                // gone stale. The play path would retry at the track change, too late
                // for a fade or a gapless join; here the retry costs nothing audible.
                AppLog.Write("playback", $"preload open of {next.VideoId} failed ({ex.Message}); retrying with fresh URL");
                media = await _streams.ResolveAsync(next.VideoId, bypassCache: true, ct);
                if (Superseded())
                    return;
                await Arm(media);
            }

            lock (_sync)
            {
                if (_currentKey != parentKey || _commandVersion != parentSeq)
                {
                    _player.ClearNext();
                    return;
                }
                _nextKey = key;
                _nextTrack = next;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write("playback", $"preload failed ({ex.Message}); Ended path will play from scratch");
        }

        bool Superseded()
        {
            lock (_sync)
                return _currentKey != parentKey || _commandVersion != parentSeq;
        }
    }

    private async Task PreloadDetachedAsync(string parentKey, int parentSeq)
    {
        // Registered BEFORE the gate wait: a seek (or command) issued while this
        // preload is still queued must find the cts to cancel, or it stalls behind
        // the full resolve + prebuffer it meant to preempt. Cancelling the previous
        // registration keeps at most one arming successor alive.
        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _activePreload?.Cancel();
            _activePreload = cts;
        }

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await PreloadNextAsync(parentKey, parentSeq, cts.Token);
        }
        finally
        {
            lock (_sync)
            {
                if (_activePreload == cts)
                    _activePreload = null;
            }
            _gate.Release();
        }
    }

    /// <summary>
    /// A seek while the track waits for the network only moves where it resumes.
    /// What the engine still had buffered plays out from the old place meanwhile:
    /// stopping it here would race the resume. Caller holds <see cref="_sync"/>.
    /// </summary>
    private void SeekWhileWaiting(TimeSpan position)
    {
        _seekWhileWaiting = position;
        _position = position;
        ProgressChanged?.Invoke(new PlaybackProgress(position, _duration, TimeSpan.Zero));
    }

    private async Task SeekCoreAsync(TimeSpan position, CancellationToken ct)
    {
        if (State is not (PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering))
            return;

        bool rearm;
        string? seekedKey;
        lock (_sync)
        {
            seekedKey = _currentKey;

            // A skip fade already moved the queue on: the track being seeked is
            // on its way out, and the fade finishes within MaxSkipFade.
            if (_skipAdvancedQueue)
                return;

            // A fade in progress has read part of the next track, which can't be
            // rewound: disarm it now and arm it again from its start once the seek
            // lands. Either way, no fade may begin while the seek is in flight —
            // progress still reports the old position until it lands.
            rearm = _crossfadeStarted && _nextKey is not null;
            if (rearm)
            {
                _disarmedKey = _nextKey;
                _disarmedTrack = _nextTrack;
                _nextKey = null;
                _nextTrack = null;
            }
            _crossfadeStarted = true;
        }
        if (rearm)
            _player.ClearNext();

        SeekResult result;
        try
        {
            result = await _player.SeekAsync(position, ct);
        }
        catch
        {
            lock (_sync) _crossfadeStarted = false;
            throw;
        }

        lock (_sync)
        {
            // From here on, a tick still in flight from the replaced chain would
            // carry the old position: it could begin a fade right after a seek
            // back from the last seconds, so only the new chain's ticks count.
            _crossfadeStarted = false;
            if (result.Chain is { } chain && seekedKey is not null)
                _seekedChain = (seekedKey, chain);

            // The cut dropped a next track that a fade this seek didn't know
            // about had started reading: arm it again, so the new end
            // fades instead of ending and playing it from its start.
            if (!result.NextKept && _nextKey is not null)
            {
                _nextKey = null;
                _nextTrack = null;
                rearm = true;
            }
            _position = position;
            ProgressChanged?.Invoke(new PlaybackProgress(position, _duration, TimeSpan.Zero));
        }

        if (rearm)
            RearmPreloadIfIdle();
    }

    private void StopCore()
    {
        lock (_sync)
        {
            _currentKey = null;
            _currentTrack = null;
            _nextKey = null;
            _nextTrack = null;
            _skipAdvancedQueue = false;
            _position = TimeSpan.Zero;
            _duration = null;
            _machine.TransitionTo(PlaybackState.Stopped);
            NowPlayingChanged?.Invoke(null);
        }
        _player.Stop();
    }

    private void OnPlayerEvent(AudioPlayerEvent e)
    {
        switch (e)
        {
            case ProgressEvent progress:
                HandleProgress(progress);
                break;
            case TrackPromotedEvent promoted:
                HandlePromoted(promoted);
                break;
            case PlaybackEndedEvent ended:
                HandleEnded(ended);
                break;
            case PlayerFaultedEvent faulted:
                HandleFault(faulted);
                break;
            case DeviceChangedEvent changed:
                Notification?.Invoke(new(PlaybackNoticeKind.OutputChanged, changed.DeviceName));
                break;
            case DeviceLostEvent:
                // Playback would stall frozen-but-"Playing"; pausing is the
                // honest state and resumes cleanly when a device returns.
                Pause();
                Notification?.Invoke(new(PlaybackNoticeKind.OutputLost));
                break;
        }
    }

    private void HandleProgress(ProgressEvent e)
    {
        var beginCrossfade = false;
        var fetchAhead = false;
        TimeSpan crossfadeDuration = default;
        PlaybackProgress progress;

        lock (_sync)
        {
            if (e.Key != _currentKey ||
                (_seekedChain is { } seeked && seeked.Key == e.Key && seeked.Chain != e.Chain))
                return;

            // While the track waits for the network, the engine still plays out
            // what it had buffered, unless a seek has said where to resume.
            _position = _seekWhileWaiting ?? e.Position;
            if (e.Duration is not null)
                _duration = e.Duration;

            if (!_crossfadeStarted && _settings.CrossfadeEnabled && _nextKey is not null &&
                _duration is { } total && total > TimeSpan.Zero)
            {
                var remaining = total - e.Position;
                var window = TimeSpan.FromSeconds(_settings.CrossfadeSeconds);
                if (remaining > TimeSpan.Zero && remaining <= window)
                {
                    _crossfadeStarted = true;
                    beginCrossfade = true;
                    crossfadeDuration = remaining; // never longer than the runway left
                }
            }

            // Starvation ↔ recovery mapping (a stall shows as Buffering, not
            // silence). Atomic with the state check — a concurrent Pause can't
            // slip between check and transition.
            if (e.IsStarved && _machine.State is PlaybackState.Playing)
                _machine.TransitionTo(PlaybackState.Buffering);
            else if (!e.IsStarved && _machine.State is PlaybackState.Buffering)
                _machine.TransitionTo(PlaybackState.Playing);

            progress = new PlaybackProgress(_position, _duration, e.Buffered);

            // Nothing armed a little way into a track: if it's the queue's last,
            // fetch what autoplay follows it with (once per track).
            if (_radio is not null && _nextKey is null && _autoplayAheadKey != e.Key &&
                e.Position >= AutoplayLead)
            {
                _autoplayAheadKey = e.Key;
                fetchAhead = true;
            }
        }

        if (fetchAhead)
            _ = AutoplayAheadAsync();

        if (beginCrossfade)
            _player.BeginCrossfade(crossfadeDuration);

        ProgressChanged?.Invoke(progress);
    }

    private void HandlePromoted(TrackPromotedEvent e)
    {
        TrackInfo promoted;
        int seq;
        var clearRearmed = false;

        lock (_sync)
        {
            if (e.Key == _disarmedKey && _disarmedTrack is { } disarmed)
            {
                // A seek disarmed this track mid-fade, but the fade had already
                // finished. Whatever was armed since was armed to follow the old track.
                clearRearmed = _nextKey is not null;
                _nextKey = e.Key;
                _nextTrack = disarmed;
            }

            if (e.Key != _nextKey || _nextTrack is null)
                return;

            _disarmedKey = null;
            _disarmedTrack = null;

            promoted = _nextTrack;
            _currentKey = e.Key;
            _currentTrack = promoted;
            _position = TimeSpan.Zero;
            _duration = promoted.Duration;
            _nextKey = null;
            _nextTrack = null;
            _crossfadeStarted = false;
            _recoveryAttempted = false;
            _playedThisSession.Add(promoted.VideoId);
            seq = _commandVersion;

            // The engine already advanced; bring the queue pointer along while the
            // lock guarantees no command issue interleaves — unless a skip fade
            // already moved it when the fade began.
            // A skip fade's promotion is the user's skip; any other is the
            // engine moving on at the end of the track.
            var skipped = _skipAdvancedQueue;
            if (skipped)
                _skipAdvancedQueue = false;
            else
                _queue.NextTrack(TrackEndReason.Natural);
            NowPlayingChanged?.Invoke(promoted);
            TrackStarted?.Invoke(new(promoted, OnItsOwn: !skipped));
        }

        if (clearRearmed)
            _player.ClearNext();
        _ = PreloadDetachedAsync(e.Key, seq);
    }

    private void HandleEnded(PlaybackEndedEvent e) =>
        // Nothing was armed (preload failed or queue was empty at preload time).
        _ = RunReactionAsync(e.Key, async (seq, ct) =>
        {
            if (_queue.NextTrack(TrackEndReason.Natural) is not null)
            {
                await PlayCurrentAsync(TimeSpan.Zero, seq, ct);
                return;
            }

            // The queue ran out. Autoplay may carry on: say so while it fetches
            // (Loading, not a silent "Playing" at the end of the track).
            if (AutoplayApplies() is not null)
            {
                lock (_sync) _machine.TransitionTo(PlaybackState.Loading);
                if (await TryContinueWithAutoplayAsync(TrackEndReason.Natural, ct))
                {
                    await PlayCurrentAsync(TimeSpan.Zero, seq, ct);
                    return;
                }
            }
            StopCore();
        });

    /// <summary>The track autoplay would continue from — the queue's last track,
    /// with nothing after it — or null when autoplay doesn't apply: no radio source,
    /// autoplay turned off, repeat on, or the queue doesn't end at the current track.</summary>
    private TrackInfo? AutoplayApplies()
    {
        if (_radio is null || !AutoplayPolicy.ShouldContinue(_settings.Autoplay, _queue.Repeat))
            return null;
        var snapshot = _queue.Snapshot();
        var last = snapshot.Tracks.Count - 1;
        return last >= 0 && snapshot.CurrentIndex == last ? snapshot.Tracks[last] : null;
    }

    /// <summary>
    /// Extends a queue that ran out with radio seeded from its last track and moves to
    /// the first added track. Returns false (queue unchanged, a toast says why) when
    /// autoplay doesn't apply, the fetch fails or nothing new came back.
    /// </summary>
    private async Task<bool> TryContinueWithAutoplayAsync(TrackEndReason reason, CancellationToken ct)
    {
        if (AutoplayApplies() is not { } seed)
            return false;

        int added;
        try
        {
            if (await RadioIdOfAsync(seed, ct) is not { } radioId)
            {
                Notification?.Invoke(new(PlaybackNoticeKind.AutoplayNoRadio, seed.Title));
                return false;
            }
            added = await ExtendWithAutoplayAsync(seed, radioId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write("autoplay", $"radio for {seed.VideoId} failed: {ex.Message}");
            Notification?.Invoke(new(PlaybackNoticeKind.AutoplayFailed, seed.Title, ex.Message));
            return false;
        }

        if (added == 0)
        {
            Notification?.Invoke(new(PlaybackNoticeKind.AutoplayEmpty, seed.Title));
            return false;
        }
        return _queue.NextTrack(reason) is not null;
    }

    /// <summary>The id whose radio continues <paramref name="seed"/>: its own on
    /// YouTube, else its match (<see cref="RadioSeedFor"/>); null for none.</summary>
    private async Task<string?> RadioIdOfAsync(TrackInfo seed, CancellationToken ct) =>
        YouTubeIds.IsVideoId(seed.VideoId) ? seed.VideoId
        : RadioSeedFor is { } seedFor && await seedFor(seed, ct) is { } id && YouTubeIds.IsVideoId(id) ? id
        : null;

    /// <summary>
    /// Appends the autoplay continuation of <paramref name="seed"/>, the radio of
    /// <paramref name="radioId"/>, to the queue. The radio request is shared: the
    /// fetch-ahead and the queue's end ask for the same seed at most once, unless
    /// the first attempt failed.
    /// </summary>
    private async Task<int> ExtendWithAutoplayAsync(TrackInfo seed, string radioId, CancellationToken ct)
    {
        Task<IReadOnlyList<TrackInfo>> fetch;
        lock (_sync)
        {
            if (_autoplayFetch is { } pending && ReferenceEquals(pending.Seed, seed) &&
                !pending.Fetch.IsFaulted && !pending.Fetch.IsCanceled)
            {
                fetch = pending.Fetch;
            }
            else
            {
                // The coordinator's lifetime, not the caller's token: a seek or a
                // superseded reaction stops waiting, but the answer is still wanted.
                fetch = _radio!.GetRadioAsync(radioId, _lifetime.Token);
                _autoplayFetch = (seed, fetch);
            }
        }

        var radio = await fetch.WaitAsync(ct);
        HashSet<string> played;
        lock (_sync) played = [.. _playedThisSession];
        var queued = _queue.Items.Select(t => t.VideoId).ToHashSet();
        // A matched local file's song would follow it as the same song again.
        queued.Add(radioId);
        return _queue.AppendAutoplay(seed, AutoplayPolicy.SelectContinuation(seed, radio, queued, played));
    }

    /// <summary>
    /// Fetch-ahead: a little way into the queue's last track, the continuation is
    /// fetched and queued, so the queue shows what autoplay will play and the first
    /// autoplay track can be armed for a gapless handover. A failure here is only
    /// logged; the queue's end tries again and says so if it fails too.
    /// </summary>
    private async Task AutoplayAheadAsync()
    {
        if (AutoplayApplies() is not { } seed)
            return;
        try
        {
            // A seed without radio is said once, at the queue's end.
            if (await RadioIdOfAsync(seed, _lifetime.Token) is not { } radioId)
                return;
            if (await ExtendWithAutoplayAsync(seed, radioId, _lifetime.Token) > 0)
                RearmPreloadIfIdle();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write("autoplay", $"fetch-ahead for {seed.VideoId} failed ({ex.Message}); the queue's end retries");
        }
    }

    private void HandleFault(PlayerFaultedEvent e) =>
        _ = RunReactionAsync(e.Key, async (seq, ct) =>
        {
            bool attemptRecovery;
            TrackInfo track;

            lock (_sync)
            {
                if (_currentTrack is null)
                    return;
                attemptRecovery = !_recoveryAttempted;
                _recoveryAttempted = true;
                track = _currentTrack;
            }

            if (attemptRecovery)
            {
                // Most common cause: the resolved URL expired mid-track.
                AppLog.Write("playback",
                    $"fault on {track.VideoId} at {_position:mm\\:ss} ({e.Reason}); re-resolving");
                try
                {
                    try
                    {
                        await ResumeCurrentAsync(e.Key, track, ct);
                        return;
                    }
                    catch (OfflineException ex)
                    {
                        // The network went away mid-track. Skipping would only fail
                        // the same way on every track after it.
                        AppLog.Write("playback", $"{track.VideoId} lost the network ({ex.Message}); waiting for it");
                        if (await WaitForNetworkAsync(e.Key, track, ct))
                            return;
                        await GiveUpOnNetworkAsync(track, seq, ct);
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }
            }

            Notification?.Invoke(new(PlaybackNoticeKind.TrackSkipped, track.Title, e.Reason));
            if (_queue.NextTrack(TrackEndReason.Manual) is null)
                StopCore();
            else
                await PlayCurrentAsync(TimeSpan.Zero, seq, ct);
        });

    /// <summary>
    /// Plays the faulted track again from a fresh resolution, where it stopped (or
    /// where a seek during the wait moved it). Gate held. A pause made meanwhile
    /// holds: the engine starts unpaused, so it is paused again at once.
    /// </summary>
    private async Task ResumeCurrentAsync(string faultedKey, TrackInfo track, CancellationToken ct)
    {
        var media = await _streams.ResolveAsync(track.VideoId, bypassCache: true, ct);
        TimeSpan position;
        TimeSpan? seekAtStart;
        lock (_sync)
        {
            // A crossfade during the play-out may have moved on to the next track.
            if (_currentKey != faultedKey)
                return;
            seekAtStart = _seekWhileWaiting;
            position = seekAtStart ?? _position;
        }

        var key = NewKey(track);
        await _player.PlayAsync(
            new AudioTrackRequest(
                key, media.Url, track.Title, track.Duration, position, Loudness(media)), ct);
        TimeSpan? lateSeek;
        bool resumedFromWait;
        lock (_sync)
        {
            _currentKey = key;
            _crossfadeStarted = false;
            // A seek that came in while the engine opened the stream.
            lateSeek = _seekWhileWaiting != seekAtStart ? _seekWhileWaiting : null;
            resumedFromWait = _waitingForNetwork;
            _waitingForNetwork = false;
            _seekWhileWaiting = null;
            if (_machine.State is PlaybackState.Paused)
                _player.Pause();
            else if (_machine.State is PlaybackState.Buffering)
                _machine.TransitionTo(PlaybackState.Playing);
        }

        if (resumedFromWait)
            AnnounceWait(null);
        if (lateSeek is { } late)
            await SeekCoreAsync(late, ct);
    }

    /// <summary>
    /// Tries the faulted track again, less often as time goes on, until the
    /// network is back (true) or <see cref="NetworkWaitLimit"/> has passed (false).
    /// The engine plays out what it had buffered meanwhile, then shows Buffering.
    /// A command (Next, Play) cancels the wait; a seek moves where it resumes.
    /// </summary>
    private async Task<bool> WaitForNetworkAsync(string faultedKey, TrackInfo track, CancellationToken ct)
    {
        lock (_sync) _waitingForNetwork = true;
        AnnounceWait(track);
        try
        {
            var waited = TimeSpan.Zero;
            for (var attempt = 0; waited < NetworkWaitLimit; attempt++)
            {
                var delay = NetworkRetryDelays[Math.Min(attempt, NetworkRetryDelays.Count - 1)];
                await _delay(delay, ct);
                waited += delay;

                lock (_sync)
                {
                    if (_currentKey != faultedKey)
                        return true; // moved on without us; nothing to resume
                }

                try
                {
                    await ResumeCurrentAsync(faultedKey, track, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not only OfflineException: a network coming back (Wi-Fi
                    // rejoining, a captive portal) can let the resolve through
                    // and still fail the stream. The limit bounds the tries.
                    AppLog.Write("playback", $"{track.VideoId} still can't resume: {ex.Message}");
                    continue;
                }

                AppLog.Write("playback", $"network back after {waited.TotalSeconds:0} s; {track.VideoId} resumed");
                // A fresh start: a later drop in the same track is worth the same patience.
                lock (_sync) _recoveryAttempted = false;
                return true;
            }

            AppLog.Write("playback", $"no network after {waited.TotalMinutes:0} min; giving up on {track.VideoId}");
            return false;
        }
        finally
        {
            lock (_sync)
            {
                _waitingForNetwork = false;
                _seekWhileWaiting = null;
            }
            AnnounceWait(null);
        }
    }

    /// <summary>Reports a change of <see cref="TrackWaitingForNetwork"/>, outside
    /// the lock; the same value again reports nothing.</summary>
    private void AnnounceWait(TrackInfo? track)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_announcedWait, track))
                return;
            _announcedWait = track;
        }
        NetworkWaitChanged?.Invoke(track);
    }

    /// <summary>The network never came back: move to the next track that plays
    /// offline, as a track that can't start does, or stop on this one.</summary>
    private async Task GiveUpOnNetworkAsync(TrackInfo track, int seq, CancellationToken ct)
    {
        if (NextOfflineIndex() is not { } index)
        {
            Notification?.Invoke(new(PlaybackNoticeKind.StoppedOffline, track.Title));
            StopCore();
            return;
        }

        Notification?.Invoke(new(PlaybackNoticeKind.SkippedOffline, track.Title));
        _queue.JumpTo(index);
        await PlayCurrentAsync(TimeSpan.Zero, seq, ct);
    }

    private string NewKey(TrackInfo track) =>
        $"{track.VideoId}#{Interlocked.Increment(ref _keyCounter)}";

    /// <summary>Normalization is a per-request decision: the setting is read at
    /// build time, so toggling it applies from the next prepared track onward.</summary>
    private double? Loudness(ResolvedMedia media) =>
        _settings.NormalizeVolume ? media.LoudnessDb : null;

    public void Dispose()
    {
        _player.EventRaised -= OnPlayerEvent;
        _lifetime.Cancel();
        lock (_sync)
        {
            _activeWork?.Cancel();
            _activePreload?.Cancel();
        }
    }
}
