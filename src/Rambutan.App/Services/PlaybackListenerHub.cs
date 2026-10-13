using System;
using System.Collections.Generic;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Integrations;

namespace Rambutan.App.Services;

/// <summary>
/// Subscribes the coordinator once and fans playback out to the integrations.
/// Progress is throttled to about once a second,
/// except that a seek is passed on at once so presence timestamps and scrobble
/// accounting see it. SMTC keeps its own wiring for now.
/// </summary>
public sealed class PlaybackListenerHub
{
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    /// <summary>How far position may run ahead of wall time before it counts as a seek.</summary>
    private static readonly TimeSpan SeekSlack = TimeSpan.FromSeconds(2);

    private readonly List<IPlaybackListener> _listeners = [];
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;
    private TimeSpan _lastSentPosition;
    private bool _progressSent;

    public PlaybackListenerHub(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public void Add(IPlaybackListener listener)
    {
        lock (_gate)
            _listeners.Add(listener);
    }

    public void Attach(PlaybackCoordinator coordinator)
    {
        coordinator.NowPlayingChanged += OnNowPlayingChanged;
        coordinator.StateChanged += OnStateChanged;
        coordinator.ProgressChanged += p => OnProgress(p.Position, p.Duration);
    }

    public void OnNowPlayingChanged(TrackInfo? track)
    {
        lock (_gate)
        {
            _progressSent = false;
            Each(l => l.OnNowPlayingChanged(track));
        }
    }

    public void OnStateChanged(PlaybackState state)
    {
        lock (_gate)
            Each(l => l.OnStateChanged(state));
    }

    public void OnProgress(TimeSpan position, TimeSpan? duration)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_progressSent)
            {
                var elapsed = now - _lastSentAt;
                var moved = position - _lastSentPosition;
                var seeked = moved < TimeSpan.Zero || moved > elapsed + SeekSlack;
                if (!seeked && elapsed < ProgressInterval)
                    return;
            }

            _progressSent = true;
            _lastSentAt = now;
            _lastSentPosition = position;
            Each(l => l.OnProgress(position, duration));
        }
    }

    // Under _gate, so each listener sees events one at a time and in order.
    private void Each(Action<IPlaybackListener> call)
    {
        foreach (var listener in _listeners)
        {
            try
            {
                call(listener);
            }
            catch (Exception ex)
            {
                AppLog.Write("integrations", $"{listener.GetType().Name} threw: {ex.Message}");
            }
        }
    }
}
