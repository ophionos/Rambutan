using System.Threading.Channels;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Integrations.Scrobbling;

namespace Rambutan.Integrations.ListenBrainz;

public enum ListenBrainzConnectionState
{
    Disconnected,

    /// <summary>A token was entered and the server is checking it.</summary>
    Connecting,

    Connected,
}

/// <summary>
/// The problem the settings page reports under ListenBrainz, as a code the app
/// words in the user's language.
/// </summary>
public enum ListenBrainzNotice
{
    /// <summary>The token entered is not valid, or is empty.</summary>
    ConnectTokenInvalid,
    /// <summary>Connecting could not reach the server.</summary>
    ConnectUnreachable,
    /// <summary>Connecting was rate-limited.</summary>
    ConnectRateLimited,
    /// <summary>The address answered, but not as a ListenBrainz server.</summary>
    ConnectNotAServer,
    /// <summary>Connecting failed for another reason the server gave.</summary>
    ConnectRefused,
    /// <summary>The server refused the saved token. Listens not yet sent are kept, and
    /// plays are still recorded until the user connects again or disconnects.</summary>
    TokenRefused,
    /// <summary>The server asked for fewer requests; listens are sent later.</summary>
    SlowedDown,
    /// <summary>The server could not be reached; listens are sent when it is back.</summary>
    Unreachable,
}

/// <summary>
/// ListenBrainz scrobbling: ties <see cref="ScrobbleGate"/>, the
/// <see cref="ScrobbleQueue"/> and <see cref="ListenBrainzClient"/> to playback,
/// as <see cref="LastFm.LastFmScrobbler"/> does for Last.fm. Now-playing is
/// fire-and-forget; every earned listen is queued first and then drained, in
/// order, on success, on connect and at startup.
/// </summary>
/// <remarks>
/// Playback callbacks only run the (pure, cheap) gate and post work; all file
/// and network I/O happens one item at a time on this object's own worker, which
/// also owns the connection, so no two calls ever race over it. Nothing is sent
/// until the user connects, and nothing is recorded while
/// <see cref="ScrobblingEnabled"/> is off. The client depends on the server, so
/// a factory makes one for the saved connection at <see cref="Start"/> and one
/// for each server given to <see cref="ConnectAsync"/>.
/// </remarks>
public sealed class ListenBrainzScrobbler : IPlaybackListener, IDisposable
{
    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan FirstRateLimitDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly Func<Uri, ListenBrainzClient> _clientFor;
    private readonly ScrobbleQueue _queue;
    private readonly IListenBrainzTokenStore _tokens;
    private readonly TimeProvider _clock;
    private readonly ScrobbleGate _gate;
    private readonly object _gateSync = new();
    private readonly object _stateSync = new();
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    // Worker-owned.
    private ListenBrainzConnection? _account;
    private ListenBrainzClient? _client;
    private TimeSpan _retryDelay;
    private DateTimeOffset? _retryAt;
    private bool _retryScheduled;
    private int _oneAtATime;

    // Read from any thread (the settings page), written by the worker.
    private ListenBrainzConnectionState _connection;
    private ListenBrainzNotice? _notice;
    private ListenBrainzConnection? _shownAccount;

    private volatile bool _scrobblingEnabled = true;

    public ListenBrainzScrobbler(
        Func<Uri, ListenBrainzClient> clientFor, ScrobbleQueue queue, IListenBrainzTokenStore tokens,
        TimeProvider? clock = null)
    {
        _clientFor = clientFor;
        _queue = queue;
        _tokens = tokens;
        _clock = clock ?? TimeProvider.System;
        _gate = new ScrobbleGate(_clock);
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Raised (on the worker) whenever connection, notice or queue size changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Whether plays are sent at all: the setting that is separate from the
    /// connection. Off, nothing is recorded; listens already queued still send.
    /// </summary>
    public bool ScrobblingEnabled
    {
        get => _scrobblingEnabled;
        set => _scrobblingEnabled = value;
    }

    public ListenBrainzConnectionState Connection
    {
        get
        {
            lock (_stateSync)
                return _connection;
        }
    }

    /// <summary>The connected account's user name, or null.</summary>
    public string? Username
    {
        get
        {
            lock (_stateSync)
                return _connection == ListenBrainzConnectionState.Connected ? _shownAccount?.Username : null;
        }
    }

    /// <summary>The connected server, or null.</summary>
    public Uri? Server
    {
        get
        {
            lock (_stateSync)
                return _connection == ListenBrainzConnectionState.Connected ? _shownAccount?.Server : null;
        }
    }

    /// <summary>The latest problem, for the settings page to word; null when all is well.</summary>
    public ListenBrainzNotice? Notice
    {
        get
        {
            lock (_stateSync)
                return _notice;
        }
    }

    public int PendingCount => _queue.Count;

    /// <summary>
    /// The server refused the token, not the user ending the connection. Plays are
    /// still recorded, and sent when the user connects again; Disconnect discards
    /// them. It outlasts a restart and a failed reconnect.
    /// </summary>
    public bool SessionEnded => _queue.SessionEnded;

    /// <summary>Loads the saved connection and queue, then sends whatever is waiting.</summary>
    public void Start() => Post(async () =>
    {
        _queue.Load();
        if (_tokens.Load() is { } saved)
        {
            _account = saved;
            _client = _clientFor(saved.Server);
            _queue.SessionEnded = false;
            SetState(ListenBrainzConnectionState.Connected, null);
        }
        else if (_queue.SessionEnded)
        {
            SetState(ListenBrainzConnectionState.Disconnected, ListenBrainzNotice.TokenRefused);
        }
        else
        {
            Changed?.Invoke();
        }
        await DrainAsync();
    });

    /// <summary>
    /// Checks the token with the server and, if it is valid, saves the connection
    /// and sends what is waiting. On failure <see cref="Notice"/> says why and the
    /// state is what it was. Queued listens belong to the service, not to the
    /// account: after the server ended a connection, connecting as another user
    /// or on another server sends them to that account.
    /// </summary>
    public Task ConnectAsync(Uri server, string token) => Run(async () =>
    {
        token = token.Trim();
        var before = _connection;
        if (token.Length == 0)
        {
            SetState(before, ListenBrainzNotice.ConnectTokenInvalid);
            return true;
        }

        SetState(ListenBrainzConnectionState.Connecting, null);
        var client = _clientFor(server);
        var result = await client.ValidateTokenAsync(token, _cts.Token);
        if (!result.IsOk)
        {
            AppLog.Write("listenbrainz", $"validate-token: {result}");
            SetState(before, ConnectFailure(result.Outcome));
            return true;
        }

        _account = new ListenBrainzConnection(server, result.Value!, token);
        _client = client;
        _tokens.Save(_account);
        _queue.SessionEnded = false;
        ResetBackoff();
        AppLog.Write("listenbrainz", $"Connected to {server.Authority}.");
        SetState(ListenBrainzConnectionState.Connected, null);
        await DrainAsync();
        return true;
    });

    /// <summary>
    /// Forgets the token and discards listens not yet sent: they belong to the
    /// account being disconnected, not to whichever connects next. After the
    /// server refused the token, this is also how the user stops recording.
    /// </summary>
    public Task DisconnectAsync() => Run(() =>
    {
        _account = null;
        _client = null;
        _tokens.Clear();
        _queue.Clear();
        ResetBackoff();
        AppLog.Write("listenbrainz", "Disconnected by the user.");
        SetState(ListenBrainzConnectionState.Disconnected, null);
        return Task.FromResult(true);
    });

    /// <summary>Completes once everything posted so far has run (for tests and shutdown).</summary>
    public Task WhenIdleAsync() => Run(() => Task.FromResult(true));

    private static ListenBrainzNotice ConnectFailure(ListenBrainzOutcome outcome) => outcome switch
    {
        ListenBrainzOutcome.InvalidToken => ListenBrainzNotice.ConnectTokenInvalid,
        ListenBrainzOutcome.RetryLater => ListenBrainzNotice.ConnectUnreachable,
        ListenBrainzOutcome.RateLimited => ListenBrainzNotice.ConnectRateLimited,
        ListenBrainzOutcome.NotAServer => ListenBrainzNotice.ConnectNotAServer,
        _ => ListenBrainzNotice.ConnectRefused,
    };

    public void OnNowPlayingChanged(TrackInfo? track)
    {
        lock (_gateSync)
            _gate.OnNowPlaying(track);

        // A track too short to scrobble is not announced either: Settings promises
        // that tracks of 30 seconds or less are not sent at all.
        if (track is not null && _scrobblingEnabled && ScrobbleGate.Threshold(track.Duration) is not null
            && ScrobbleEntries.From(track, _clock.GetUtcNow()) is { } entry)
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
            && ScrobbleEntries.From(earned.Track, earned.StartedAt, earned.Duration) is { } entry)
        {
            Post(async () =>
            {
                // Never connected, or disconnected by the user: nothing is
                // recorded. Ended by the server: kept for the reconnect.
                if (_account is null && !_queue.SessionEnded)
                    return;
                _queue.Enqueue(entry);
                Changed?.Invoke();
                if (_account is not null)
                    await DrainAsync();
            });
        }
    }

    private async Task SendNowPlayingAsync(ScrobbleEntry entry)
    {
        if (_account is not { } account || _client is not { } client || InBackoff())
            return;

        var result = await client.SubmitAsync(account.Token, ListenType.PlayingNow, [entry], _cts.Token);
        if (result.IsOk)
            return;

        AppLog.Write("listenbrainz", $"playing_now: {result}");
        if (result.Outcome == ListenBrainzOutcome.InvalidToken)
            LoseConnection();
    }

    private async Task DrainAsync()
    {
        while (_account is { } account && _client is { } client && !InBackoff())
        {
            var batch = _queue.Peek(_oneAtATime > 0 ? 1 : ListenBrainzClient.MaxBatch);
            if (batch.Count == 0)
                return;

            var result = await client.SubmitAsync(
                account.Token, batch.Count == 1 ? ListenType.Single : ListenType.Import, batch, _cts.Token);
            switch (result.Outcome)
            {
                case ListenBrainzOutcome.Ok:
                    _queue.RemoveOldest(batch.Count);
                    if (_oneAtATime > 0)
                        _oneAtATime--;
                    ResetBackoff();
                    SetState(_connection, null);
                    break;

                case ListenBrainzOutcome.Rejected when batch.Count > 1:
                    // One bad play should not cost the others: resend them singly
                    // so only the refused one is dropped.
                    _oneAtATime = batch.Count;
                    break;

                case ListenBrainzOutcome.Rejected:
                    AppLog.Write("listenbrainz",
                        $"Dropped a listen the server refused ({batch[0].Artist} - {batch[0].Track}): {result}");
                    _queue.RemoveOldest(1);
                    if (_oneAtATime > 0)
                        _oneAtATime--;
                    Changed?.Invoke();
                    break;

                case ListenBrainzOutcome.InvalidToken:
                    LoseConnection();
                    return;

                case ListenBrainzOutcome.RateLimited:
                    AppLog.Write("listenbrainz", $"submit-listens: {result}");
                    BackOff(rateLimited: true);
                    return;

                default:
                    AppLog.Write("listenbrainz", $"submit-listens: {result}");
                    BackOff(rateLimited: false);
                    return;
            }
        }
    }

    /// <summary>The token was refused. Queued listens stay for the reconnect,
    /// and later plays join them (<see cref="SessionEnded"/>).</summary>
    private void LoseConnection()
    {
        AppLog.Write("listenbrainz", "Token refused; disconnected. Plays are kept until the user connects again or disconnects.");
        _account = null;
        _client = null;
        _tokens.Clear();
        _queue.SessionEnded = true;
        SetState(ListenBrainzConnectionState.Disconnected, ListenBrainzNotice.TokenRefused);
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
        SetState(_connection, rateLimited ? ListenBrainzNotice.SlowedDown : ListenBrainzNotice.Unreachable);

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

    private void SetState(ListenBrainzConnectionState connection, ListenBrainzNotice? notice)
    {
        lock (_stateSync)
        {
            _connection = connection;
            _notice = notice;
            _shownAccount = _account;
        }
        Changed?.Invoke();
    }

    private void Post(Func<Task> work)
    {
        if (!_work.Writer.TryWrite(work))
            AppLog.Write("listenbrainz", "Work posted after shutdown was dropped.");
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
                AppLog.Write("listenbrainz", $"Worker item failed: {ex.Message}");
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
