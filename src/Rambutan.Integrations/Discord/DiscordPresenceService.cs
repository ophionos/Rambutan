using System.Globalization;
using System.Text.Json;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.Integrations.Discord;

/// <summary>
/// Discord Rich Presence over Discord's local IPC socket. The
/// listener callbacks only update <see cref="DiscordPresenceTracker"/> under a
/// short lock and wake the worker; connecting, handshaking and sending all run on
/// the worker task. Off until <see cref="Configure"/> turns it on, and inert when
/// the build has no Discord application id.
/// </summary>
public sealed class DiscordPresenceService : IPlaybackListener, IDisposable
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GoodbyeTimeout = TimeSpan.FromSeconds(1);
    private const string NotRunning = "Discord isn't running";

    private readonly string? _appId;
    private readonly Func<CancellationToken, Task<Stream?>> _connect;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly DiscordPresenceTracker _tracker = new();

    private DiscordPresenceOptions _options = new();
    private TaskCompletionSource _wake = NewWake();
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private DiscordConnectionState _connection;
    private string? _detail;
    private bool _refused;
    private string? _refusal;
    private string? _lastCommandError;
    private int _nonce;

    /// <param name="appId">The Discord application id; null disables the service
    /// for this build (see <see cref="IntegrationKeys.DiscordAppId"/>).</param>
    /// <param name="connect">Opens the IPC stream; tests pass a fake Discord.</param>
    public DiscordPresenceService(
        string? appId,
        Func<CancellationToken, Task<Stream?>>? connect = null,
        TimeProvider? clock = null)
    {
        _appId = string.IsNullOrWhiteSpace(appId) ? null : appId;
        _connect = connect ?? DiscordIpcPaths.ConnectAsync;
        _clock = clock ?? TimeProvider.System;
    }

    public bool IsAvailable => _appId is not null;

    public DiscordConnectionState ConnectionState
    {
        get
        {
            lock (_gate)
                return _connection;
        }
    }

    /// <summary>The last attempt reached Discord and Discord turned the handshake
    /// down (a bad application id, say).</summary>
    public bool IsRefused
    {
        get
        {
            lock (_gate)
                return _refused;
        }
    }

    /// <summary>Discord's own reason for a refusal, untranslated; null when it
    /// gave none or when <see cref="IsRefused"/> is false.</summary>
    public string? Refusal
    {
        get
        {
            lock (_gate)
                return _refusal;
        }
    }

    /// <summary>Raised on the worker thread whenever <see cref="ConnectionState"/>
    /// or the refusal changes.</summary>
    public event Action? ConnectionChanged;

    /// <summary>Turns presence on or off and sets what it shows. Cheap and
    /// non-blocking: the worker picks the change up.</summary>
    public void Configure(bool enabled, DiscordPresenceOptions options)
    {
        lock (_gate)
        {
            _options = options;
            if (enabled && IsAvailable && _run is null)
            {
                _run = new CancellationTokenSource();
                var previous = _loop;
                var token = _run.Token;
                _loop = Task.Run(() => RunAsync(previous, token));
            }
            else if (!enabled && _run is not null)
            {
                _run.Cancel();
                _run.Dispose();
                _run = null;
            }

            _wake.TrySetResult();
        }
    }

    public void OnNowPlayingChanged(TrackInfo? track)
    {
        lock (_gate)
        {
            _tracker.OnNowPlaying(track);
            _wake.TrySetResult();
        }
    }

    public void OnStateChanged(PlaybackState state)
    {
        lock (_gate)
        {
            _tracker.OnState(state, _clock.GetUtcNow());
            _wake.TrySetResult();
        }
    }

    public void OnProgress(TimeSpan position, TimeSpan? duration)
    {
        lock (_gate)
        {
            _tracker.OnProgress(position, duration, _clock.GetUtcNow());
            _wake.TrySetResult();
        }
    }

    public void Dispose()
    {
        Task loop;
        lock (_gate)
        {
            _run?.Cancel();
            _run?.Dispose();
            _run = null;
            loop = _loop;
        }

        // Closing the socket clears the presence anyway; give the goodbye a moment.
        try
        {
            loop.Wait(GoodbyeTimeout + TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException)
        {
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task RunAsync(Task previous, CancellationToken ct)
    {
        // A quick off-then-on must not let the old connection's goodbye clear
        // the new connection's presence.
        try
        {
            await previous;
        }
        catch
        {
            // Already logged by that run.
        }

        var backoff = new DiscordReconnectBackoff();
        while (!ct.IsCancellationRequested)
        {
            Stream? stream = null;
            string? failure;
            var refused = false;
            string? reason = null;
            try
            {
                stream = await _connect(ct);
                (failure, refused, reason) = stream is null ? (NotRunning, false, null) : await HandshakeAsync(stream, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (stream is not null)
                    await stream.DisposeAsync();
                break;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            if (failure is null && stream is not null)
            {
                backoff.Reset();
                SetConnection(DiscordConnectionState.Connected, "connected", refused: false, reason: null);
                failure = await ServeAsync(stream, ct);
            }

            if (stream is not null)
                await stream.DisposeAsync();
            if (ct.IsCancellationRequested)
                break;

            SetConnection(DiscordConnectionState.Waiting, failure ?? NotRunning, refused, reason);
            try
            {
                await Task.Delay(backoff.Next(), _clock, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SetConnection(DiscordConnectionState.Off, "off", refused: false, reason: null);
    }

    /// <summary>Null on success, else why not (for the log), whether Discord
    /// itself said no, and Discord's own reason if it gave one.</summary>
    private async Task<(string? Failure, bool Refused, string? Reason)> HandshakeAsync(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        try
        {
            await WriteFrameAsync(stream, null, DiscordOpcode.Handshake, DiscordIpcFrame.HandshakeJson(_appId!), timeout.Token);
            var frame = await DiscordIpcFrame.ReadAsync(stream, timeout.Token);
            if (frame is not { } f)
                return ("Discord closed the connection during the handshake", false, null);

            var reply = DiscordIpcFrame.ParseIncoming(f.Payload);
            if (f.Opcode == DiscordOpcode.Close)
                return ($"refused: {reply?.Message ?? "no reason given"}", true, reply?.Message);
            if (f.Opcode == DiscordOpcode.Frame && reply?.Evt == "READY")
                return (null, false, null);
            return ($"unexpected handshake reply (opcode {f.Opcode}, event {reply?.Evt ?? "none"})", false, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("Discord didn't answer the handshake", false, null);
        }
    }

    /// <summary>Keeps Discord in step with the tracker until the connection drops
    /// (returns why) or the service is turned off (clears, then returns null).</summary>
    private async Task<string?> ServeAsync(Stream stream, CancellationToken ct)
    {
        using var writeLock = new SemaphoreSlim(1, 1);
        // Not linked to ct: a reader stopped by the same cancellation could win
        // the WhenAny below and read as a lost connection, skipping the goodbye.
        using var stopReading = new CancellationTokenSource();
        var reader = ReadLoopAsync(stream, writeLock, stopReading.Token);
        var budget = new DiscordSendBudget();
        DiscordActivity? shown = null; // a fresh connection shows nothing of ours

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var now = _clock.GetUtcNow();
                Task wake;
                DiscordActivity? desired;
                lock (_gate)
                {
                    if (_wake.Task.IsCompleted)
                        _wake = NewWake();
                    wake = _wake.Task;
                    desired = _tracker.Desired(now, _options);
                }

                TimeSpan? wait = null;
                if (!Equals(desired, shown))
                {
                    var slot = budget.WaitBefore(now);
                    if (slot == TimeSpan.Zero)
                    {
                        await SendActivityAsync(stream, writeLock, desired, ct);
                        budget.Record(now);
                        shown = desired;
                        continue;
                    }

                    wait = slot;
                }

                using var pause = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timer = Task.Delay(wait ?? Timeout.InfiniteTimeSpan, _clock, pause.Token);
                var woke = await Task.WhenAny(wake, timer, reader);
                pause.Cancel();
                ct.ThrowIfCancellationRequested();
                if (woke == reader)
                    return await reader;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await GoodbyeAsync(stream, writeLock, shown is not null);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            stopReading.Cancel();
            try
            {
                await reader;
            }
            catch
            {
                // Its outcome no longer matters.
            }
        }
    }

    private async Task GoodbyeAsync(Stream stream, SemaphoreSlim writeLock, bool clear)
    {
        using var timeout = new CancellationTokenSource(GoodbyeTimeout);
        try
        {
            if (clear)
                await SendActivityAsync(stream, writeLock, null, timeout.Token);
            await WriteFrameAsync(stream, writeLock, DiscordOpcode.Close, "{}", timeout.Token);
            // Logged so an "off" that Discord shows late can be timed against
            // when the clear left.
            AppLog.Write("discord", clear ? "goodbye sent: presence cleared, pipe closed" : "goodbye sent: pipe closed");
        }
        catch (Exception ex)
        {
            // Discord drops our presence when the socket closes regardless.
            AppLog.Write("discord", $"goodbye not sent ({ex.Message}); closing the pipe anyway");
        }
    }

    private Task SendActivityAsync(Stream stream, SemaphoreSlim writeLock, DiscordActivity? activity, CancellationToken ct)
    {
        var nonce = Interlocked.Increment(ref _nonce).ToString(CultureInfo.InvariantCulture);
        var json = DiscordIpcFrame.SetActivityJson(Environment.ProcessId, activity, nonce);
        return WriteFrameAsync(stream, writeLock, DiscordOpcode.Frame, json, ct);
    }

    private static async Task WriteFrameAsync(
        Stream stream, SemaphoreSlim? writeLock, DiscordOpcode opcode, string json, CancellationToken ct)
    {
        var frame = DiscordIpcFrame.Encode(opcode, json);
        if (writeLock is not null)
            await writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(frame, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            writeLock?.Release();
        }
    }

    /// <summary>Answers pings and notices errors and hang-ups; returns why the
    /// connection ended.</summary>
    private async Task<string> ReadLoopAsync(Stream stream, SemaphoreSlim writeLock, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                if (await DiscordIpcFrame.ReadAsync(stream, ct) is not { } frame)
                    return "Discord closed the connection";

                switch (frame.Opcode)
                {
                    case DiscordOpcode.Ping:
                        await WriteFrameAsync(stream, writeLock, DiscordOpcode.Pong,
                            System.Text.Encoding.UTF8.GetString(frame.Payload), ct);
                        break;
                    case DiscordOpcode.Close:
                        return DiscordIpcFrame.ParseIncoming(frame.Payload)?.Message ?? "Discord closed the connection";
                    case DiscordOpcode.Frame:
                        NoteCommandReply(DiscordIpcFrame.ParseIncoming(frame.Payload));
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "stopped";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>A rejected SET_ACTIVITY is logged once per distinct message, so a
    /// field the client dislikes leaves a trace without flooding the log.</summary>
    private void NoteCommandReply(DiscordIncoming? reply)
    {
        if (reply?.Evt != "ERROR")
            return;

        var message = reply.Data is { ValueKind: JsonValueKind.Object } data
                      && data.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
        message ??= "unknown error";
        lock (_gate)
        {
            if (message == _lastCommandError)
                return;
            _lastCommandError = message;
        }

        AppLog.Write("discord", $"{reply.Cmd ?? "command"} rejected: {message}");
    }

    /// <summary>Logs and announces transitions only; a retry that fails the same
    /// way as the last one stays silent.</summary>
    private void SetConnection(DiscordConnectionState state, string detail, bool refused, string? reason)
    {
        lock (_gate)
        {
            if (_connection == state && _detail == detail && _refused == refused && _refusal == reason)
                return;
            _connection = state;
            _detail = detail;
            _refused = refused;
            _refusal = reason;
        }

        AppLog.Write("discord", state == DiscordConnectionState.Waiting ? $"waiting: {detail}" : detail);
        ConnectionChanged?.Invoke();
    }
}
