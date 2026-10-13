using Rambutan.Core.Diagnostics;

namespace Rambutan.Core.Playback;

/// <summary>
/// What hover warm-up asks of the stream layer: resolve a track's stream
/// ahead of a likely play, without downloading any audio. Streams implements it.
/// </summary>
public interface IStreamWarmer
{
    /// <summary>True when playing this track needs no resolution: a usable resolved
    /// URL is at hand, or the audio is on disk.</summary>
    bool IsWarm(string videoId);

    /// <summary>Resolves the stream so a play that follows finds it ready. Cancelling
    /// abandons the resolution unless a play has since asked for it too.</summary>
    Task WarmAsync(string videoId, CancellationToken ct);
}

/// <summary>
/// Hover warm-up scheduling, apart from any I/O. The pointer resting on a
/// track row for <see cref="Dwell"/> starts resolving that track's stream, so a click
/// that follows skips the yt-dlp round trip. Only the row under the pointer counts:
/// entering another row, or leaving this one, cancels the wait and any resolution it
/// started. Tracks that are already warm are skipped, and at most
/// <see cref="MaxConcurrent"/> resolutions run at once (a cancelled one holds its slot
/// until it has actually stopped), so sweeping the pointer down a list costs nothing.
/// </summary>
public sealed class HoverWarmup : IDisposable
{
    /// <summary>Long enough that passing over a row on the way somewhere else
    /// doesn't count, short enough to start well before a deliberate click lands.</summary>
    public static readonly TimeSpan DefaultDwell = TimeSpan.FromMilliseconds(400);

    public const int DefaultMaxConcurrent = 2;

    private readonly IStreamWarmer _warmer;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _slots;
    private readonly object _sync = new();

    private string? _hovered;
    private CancellationTokenSource? _hoverCts;
    private Task _last = Task.CompletedTask;

    /// <param name="delay">Waits out the dwell; <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// unless a test drives time itself.</param>
    public HoverWarmup(
        IStreamWarmer warmer, TimeSpan? dwell = null, int maxConcurrent = DefaultMaxConcurrent,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);
        _warmer = warmer;
        Dwell = dwell ?? DefaultDwell;
        MaxConcurrent = maxConcurrent;
        _delay = delay ?? Task.Delay;
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public TimeSpan Dwell { get; }

    public int MaxConcurrent { get; }

    /// <summary>The most recent hover's work (dwell, then resolution); for tests.</summary>
    public Task Last
    {
        get { lock (_sync) return _last; }
    }

    /// <summary>The pointer came to rest on a row showing <paramref name="videoId"/>.
    /// Replaces whatever row was hovered before.</summary>
    public void Enter(string videoId)
    {
        if (string.IsNullOrEmpty(videoId))
            return;

        lock (_sync)
        {
            if (_hovered == videoId && _hoverCts is not null)
                return;
            CancelHoverUnsafe();
            _hovered = videoId;
            _hoverCts = new CancellationTokenSource();
            _last = RunAsync(videoId, _hoverCts.Token);
        }
    }

    /// <summary>The pointer left the row showing <paramref name="videoId"/> (or the row
    /// went away). A stale leave, for a row already replaced, changes nothing.</summary>
    public void Leave(string videoId)
    {
        lock (_sync)
        {
            if (_hovered != videoId)
                return;
            CancelHoverUnsafe();
        }
    }

    private void CancelHoverUnsafe()
    {
        // Not disposed: the run still holds its token, and a timer-free source
        // has nothing to release.
        _hoverCts?.Cancel();
        _hoverCts = null;
        _hovered = null;
    }

    private async Task RunAsync(string videoId, CancellationToken ct)
    {
        try
        {
            await _delay(Dwell, ct);
            if (_warmer.IsWarm(videoId))
                return;

            await _slots.WaitAsync(ct);
            try
            {
                // Checked again: the slot may have been waited for while a play
                // (or an earlier warm-up) resolved this very track.
                if (!_warmer.IsWarm(videoId))
                {
                    AppLog.Write("warmup", $"resolving {videoId} ahead of a play");
                    await _warmer.WarmAsync(videoId, ct);
                }
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Best effort: a play that follows resolves (and reports) on its own.
            AppLog.Write("warmup", $"warm-up of {videoId} failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_sync)
            CancelHoverUnsafe();
    }
}
