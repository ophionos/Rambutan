using System.Threading.Channels;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.Integrations.LastFm;

public enum LastFmConnection
{
    Disconnected,

    /// <summary>A token was issued and the browser opened; waiting for the user to approve it.</summary>
    AwaitingApproval,

    Connected,
}

/// <summary>
/// The problem the settings page reports under Last.fm, as a code the app
/// words in the user's language.
/// </summary>
public enum LastFmNotice
{
    /// <summary>Finish was chosen before the user approved Rambutan on Last.fm.</summary>
    NotApprovedYet,
    /// <summary>The approval token expired; connecting starts over.</summary>
    ApprovalExpired,
    /// <summary>Connecting couldn't reach Last.fm.</summary>
    ConnectUnreachable,
    /// <summary>Connecting was rate-limited.</summary>
    ConnectRateLimited,
    /// <summary>Connecting failed because Last.fm refused this build's API key.</summary>
    ConnectKeyRefused,
    /// <summary>Connecting failed for another reason Last.fm gave.</summary>
    ConnectRefused,
    /// <summary>Scrobbling hit a refused API key; scrobbles are kept.</summary>
    KeyRefused,
    /// <summary>Last.fm revoked the session. Scrobbles not yet sent are kept, and
    /// plays are still recorded until the user connects again or disconnects.</summary>
    SessionEnded,
    /// <summary>Last.fm asked for fewer requests; scrobbles are sent later.</summary>
    SlowedDown,
    /// <summary>Last.fm couldn't be reached; scrobbles are sent when it's back.</summary>
    Unreachable,
}

/// <summary>
/// Last.fm scrobbling: ties <see cref="ScrobbleGate"/>, the
/// <see cref="ScrobbleQueue"/> and <see cref="LastFmClient"/> to playback.
/// Now-playing is fire-and-forget; every earned scrobble is queued first and
/// then drained, in order, on success, on reconnect and at startup.
/// </summary>
/// <remarks>
/// Playback callbacks only run the (pure, cheap) gate and post work; all file
/// and network I/O happens one item at a time on this object's own worker, which
/// also owns the session, so no two calls ever race over it. Nothing is sent
/// until the user connects, and nothing is recorded while
/// <see cref="ScrobblingEnabled"/> is off.
/// </remarks>
public sealed class LastFmScrobbler : IPlaybackListener, IDisposable
{
    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan FirstRateLimitDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly LastFmClient _client;
    private readonly ScrobbleQueue _queue;
    private readonly ILastFmSessionStore _sessions;
    private readonly TimeProvider _clock;
    private readonly ScrobbleGate _gate;
    private readonly object _gateSync = new();
    private readonly object _stateSync = new();
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    // Worker-owned.
    private LastFmSession? _session;
    private string? _pendingToken;
    private TimeSpan _retryDelay;
    private DateTimeOffset? _retryAt;
    private bool _retryScheduled;
    private bool _misconfigured;
    private int _oneAtATime;

    // Read from any thread (the settings page), written by the worker.
    private LastFmConnection _connection;
    private LastFmNotice? _notice;

    private volatile bool _scrobblingEnabled = true;

    public LastFmScrobbler(
        LastFmClient client, ScrobbleQueue queue, ILastFmSessionStore sessions, TimeProvider? clock = null)
    {
        _client = client;
        _queue = queue;
        _sessions = sessions;
        _clock = clock ?? TimeProvider.System;
        _gate = new ScrobbleGate(_clock);
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Raised (on the worker) whenever connection, notice or queue size changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Whether plays are sent at all: the setting that's separate from the
    /// connection. Off, nothing is recorded; scrobbles already queued still send.
    /// </summary>
    public bool ScrobblingEnabled
    {
        get => _scrobblingEnabled;
        set => _scrobblingEnabled = value;
    }

    public LastFmConnection Connection
    {
        get
        {
            lock (_stateSync)
                return _connection;
        }
    }

    /// <summary>The connected account, or null.</summary>
    public string? Username
    {
        get
        {
            lock (_stateSync)
                return _connection == LastFmConnection.Connected ? _session?.Username : null;
        }
    }

    /// <summary>The latest problem, for the settings page to word; null when all is well.</summary>
    public LastFmNotice? Notice
    {
        get
        {
            lock (_stateSync)
                return _notice;
        }
    }

    public int PendingCount => _queue.Count;

    /// <summary>
    /// Last.fm ended the connection (error 9), not the user. Plays are still
    /// recorded, and sent when the user connects again; Disconnect discards them
    /// (maintainer, 2026-10-05). It outlasts a restart and a failed
    /// reconnect.
    /// </summary>
    public bool SessionEnded => _queue.SessionEnded;

    /// <summary>Loads the saved session and queue, then sends whatever is waiting.</summary>
    public void Start() => Post(async () =>
    {
        _queue.Load();
        if (_sessions.Load() is { } saved)
        {
            _session = saved;
            _queue.SessionEnded = false;
            SetState(LastFmConnection.Connected, null);
        }
        else if (_queue.SessionEnded)
        {
            SetState(LastFmConnection.Disconnected, LastFmNotice.SessionEnded);
        }
        else
        {
            Changed?.Invoke();
        }
        await DrainAsync();
    });

    /// <summary>
    /// Step one of the desktop auth flow: gets a token and returns the page where
    /// the user approves it (the caller opens it in the browser), or null on
    /// failure with <see cref="Notice"/> saying why.
    /// </summary>
    public Task<string?> BeginConnectAsync() => Run(async () =>
    {
        var token = await _client.GetTokenAsync(_cts.Token);
        if (!token.IsOk)
        {
            AppLog.Write("lastfm", $"auth.getToken: {token}");
            SetState(_connection, ConnectFailure(token.Outcome));
            return null;
        }

        _pendingToken = token.Value;
        SetState(LastFmConnection.AwaitingApproval, null);
        return _client.AuthorizeUrl(token.Value!);
    });

    /// <summary>
    /// Step two: exchanges the approved token for a session. Stays in
    /// <see cref="LastFmConnection.AwaitingApproval"/> when the user hasn't approved yet.
    /// </summary>
    public Task FinishConnectAsync() => Run(async () =>
    {
        if (_pendingToken is not { } token)
            return true;

        var result = await _client.GetSessionAsync(token, _cts.Token);
        switch (result.Outcome)
        {
            case LastFmOutcome.Ok:
                _pendingToken = null;
                _session = result.Value;
                _sessions.Save(result.Value!);
                _queue.SessionEnded = false;
                ResetBackoff();
                _misconfigured = false;
                AppLog.Write("lastfm", "Connected.");
                SetState(LastFmConnection.Connected, null);
                await DrainAsync();
                break;
            case LastFmOutcome.NotApproved:
                SetState(LastFmConnection.AwaitingApproval, LastFmNotice.NotApprovedYet);
                break;
            case LastFmOutcome.TokenExpired:
                _pendingToken = null;
                SetState(LastFmConnection.Disconnected, LastFmNotice.ApprovalExpired);
                break;
            default:
                AppLog.Write("lastfm", $"auth.getSession: {result}");
                SetState(LastFmConnection.AwaitingApproval, ConnectFailure(result.Outcome));
                break;
        }
        return true;
    });

    public Task CancelConnectAsync() => Run(() =>
    {
        _pendingToken = null;
        if (_connection == LastFmConnection.AwaitingApproval)
            SetState(LastFmConnection.Disconnected, _queue.SessionEnded ? LastFmNotice.SessionEnded : null);
        return Task.FromResult(true);
    });

    /// <summary>
    /// Forgets the session and discards scrobbles not yet sent: they belong to
    /// the account being disconnected, not to whichever connects next. After
    /// Last.fm ended the connection, this is also how the user stops recording.
    /// </summary>
    public Task DisconnectAsync() => Run(() =>
    {
        _pendingToken = null;
        _session = null;
        _sessions.Clear();
        _queue.Clear();
        ResetBackoff();
        AppLog.Write("lastfm", "Disconnected by the user.");
        SetState(LastFmConnection.Disconnected, null);
        return Task.FromResult(true);
    });

    /// <summary>Completes once everything posted so far has run (for tests and shutdown).</summary>
    public Task WhenIdleAsync() => Run(() => Task.FromResult(true));

    private static LastFmNotice ConnectFailure(LastFmOutcome outcome) => outcome switch
    {
        LastFmOutcome.RetryLater => LastFmNotice.ConnectUnreachable,
        LastFmOutcome.RateLimited => LastFmNotice.ConnectRateLimited,
        LastFmOutcome.ClientMisconfigured => LastFmNotice.ConnectKeyRefused,
        _ => LastFmNotice.ConnectRefused,
    };

    public void OnNowPlayingChanged(TrackInfo? track)
    {
        lock (_gateSync)
            _gate.OnNowPlaying(track);

        // A track too short to scrobble isn't announced either: Settings promises
        // that tracks of 30 seconds or less aren't sent at all.
        if (track is not null && _scrobblingEnabled && ScrobbleGate.Threshold(track.Duration) is not null
            && ToEntry(track, _clock.GetUtcNow()) is { } entry)
            Post(() => SendNowPlayingAsync(entry));
    }

    public void OnStateChanged(PlaybackState state)
    {
        lock (_gateSync)
            _gate.OnStateChanged(state);
    }

    public void OnProgress(TimeSpan position, TimeSpan? duration)
    {
        ScrobbleCandidate? earned;
        lock (_gateSync)
            earned = _gate.OnProgress(position, duration);

        if (earned is not null && _scrobblingEnabled
            && ToEntry(earned.Track, earned.StartedAt, earned.Duration) is { } entry)
        {
            Post(async () =>
            {
                // Never connected, or disconnected by the user: nothing is
                // recorded. Ended by Last.fm: kept for the reconnect.
                if (_session is null && !_queue.SessionEnded)
                    return;
                _queue.Enqueue(entry);
                Changed?.Invoke();
                if (_session is not null)
                    await DrainAsync();
            });
        }
    }

    /// <summary>
    /// What Last.fm receives for a track. The first credited artist only: YouTube
    /// Music lists features as separate credits, and "A, B" would make Last.fm
    /// invent an artist called that.
    /// </summary>
    public static ScrobbleEntry? ToEntry(TrackInfo track, DateTimeOffset startedAt, TimeSpan? duration = null)
    {
        var artist = track.Artists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Name))?.Name;
        if (artist is null || string.IsNullOrWhiteSpace(track.Title))
            return null;

        var length = duration ?? track.Duration;
        return new ScrobbleEntry
        {
            Artist = artist,
            Track = track.Title,
            Album = string.IsNullOrWhiteSpace(track.Album) ? null : track.Album,
            DurationSeconds = length is { } d && d > TimeSpan.Zero ? (int)Math.Round(d.TotalSeconds) : null,
            Timestamp = startedAt.ToUnixTimeSeconds(),
        };
    }

    private async Task SendNowPlayingAsync(ScrobbleEntry entry)
    {
        if (_session is not { } session || _misconfigured || InBackoff())
            return;

        var result = await _client.UpdateNowPlayingAsync(session.Key, entry, _cts.Token);
        if (result.IsOk)
            return;

        AppLog.Write("lastfm", $"track.updateNowPlaying: {result}");
        if (result.Outcome == LastFmOutcome.InvalidSession)
            LoseSession();
    }

    private async Task DrainAsync()
    {
        while (_session is { } session && !_misconfigured && !InBackoff())
        {
            var batch = _queue.Peek(_oneAtATime > 0 ? 1 : LastFmClient.MaxBatch);
            if (batch.Count == 0)
                return;

            var result = await _client.ScrobbleAsync(session.Key, batch, _cts.Token);
            switch (result.Outcome)
            {
                case LastFmOutcome.Ok:
                    _queue.RemoveOldest(batch.Count);
                    if (_oneAtATime > 0)
                        _oneAtATime--;
                    if (result.Value.Ignored > 0)
                        AppLog.Write("lastfm", $"Last.fm ignored {result.Value.Ignored} of {batch.Count} scrobbles.");
                    ResetBackoff();
                    SetState(_connection, null);
                    break;

                case LastFmOutcome.Rejected when batch.Count > 1:
                    // One bad play shouldn't cost the others: resend them singly
                    // so only the refused one is dropped.
                    _oneAtATime = batch.Count;
                    break;

                case LastFmOutcome.Rejected:
                    AppLog.Write("lastfm",
                        $"Dropped a scrobble Last.fm refused ({batch[0].Artist} - {batch[0].Track}): {result}");
                    _queue.RemoveOldest(1);
                    if (_oneAtATime > 0)
                        _oneAtATime--;
                    Changed?.Invoke();
                    break;

                case LastFmOutcome.InvalidSession:
                    LoseSession();
                    return;

                case LastFmOutcome.ClientMisconfigured:
                    AppLog.Write("lastfm", $"track.scrobble: {result}");
                    _misconfigured = true;
                    SetState(_connection, LastFmNotice.KeyRefused);
                    return;

                case LastFmOutcome.RateLimited:
                    AppLog.Write("lastfm", $"track.scrobble: {result}");
                    BackOff(rateLimited: true);
                    return;

                default:
                    AppLog.Write("lastfm", $"track.scrobble: {result}");
                    BackOff(rateLimited: false);
                    return;
            }
        }
    }

    /// <summary>Error 9: the key was revoked. Queued scrobbles stay for the
    /// reconnect, and later plays join them (<see cref="SessionEnded"/>).</summary>
    private void LoseSession()
    {
        AppLog.Write("lastfm", "Session key rejected (error 9); disconnected. Plays are kept until the user connects again or disconnects.");
        _session = null;
        _sessions.Clear();
        _queue.SessionEnded = true;
        SetState(LastFmConnection.Disconnected, LastFmNotice.SessionEnded);
    }

    private bool InBackoff() => _retryAt is { } at && _clock.GetUtcNow() < at;

    private void ResetBackoff()
    {
        _retryDelay = TimeSpan.Zero;
        _retryAt = null;
    }

    private void BackOff(bool rateLimited)
    {
        var first = rateLimited ? FirstRateLimitDelay : FirstRetryDelay;
        var doubled = _retryDelay * 2;
        _retryDelay = doubled < first ? first : doubled > MaxRetryDelay ? MaxRetryDelay : doubled;
        _retryAt = _clock.GetUtcNow() + _retryDelay;
        SetState(_connection, rateLimited ? LastFmNotice.SlowedDown : LastFmNotice.Unreachable);

        ScheduleRetry(_retryDelay);
    }

    /// <summary>One retry timer at a time; a later backoff moves the target, not the timer.</summary>
    private void ScheduleRetry(TimeSpan delay)
    {
        if (_retryScheduled)
            return;
        _retryScheduled = true;
        _ = RetryLaterAsync(delay);
    }

    private async Task RetryLaterAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _clock, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Post(() =>
        {
            _retryScheduled = false;
            // Early: the timer ran a little ahead of the clock, or a backoff since
            // it was set moved the target later. Wait out the rest; draining now
            // would do nothing and leave no timer behind.
            if (_retryAt is { } at && _clock.GetUtcNow() < at)
            {
                ScheduleRetry(at - _clock.GetUtcNow());
                return Task.CompletedTask;
            }
            return DrainAsync();
        });
    }

    private void SetState(LastFmConnection connection, LastFmNotice? notice)
    {
        lock (_stateSync)
        {
            _connection = connection;
            _notice = notice;
        }
        Changed?.Invoke();
    }

    private void Post(Func<Task> work)
    {
        if (!_work.Writer.TryWrite(work))
            AppLog.Write("lastfm", "Work posted after shutdown was dropped.");
    }

    private Task<T> Run<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try
            {
                done.TrySetResult(await work());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
                throw;
            }
        });
        return done.Task;
    }

    private async Task PumpAsync()
    {
        await foreach (var work in _work.Reader.ReadAllAsync())
        {
            try
            {
                await work();
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Write("lastfm", $"Worker item failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _work.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _pump.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Shutting down; the worker's own handler already logged.
        }
        _cts.Dispose();
    }
}
