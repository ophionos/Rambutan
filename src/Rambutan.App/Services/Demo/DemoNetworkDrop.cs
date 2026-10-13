using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Playback;

namespace Rambutan.App.Services.Demo;

/// <summary>
/// The demo's network drop (<c>RAMBUTAN_DEMO_FAIL=network-drop</c>): twenty seconds
/// into each track, the first time it plays, the stream "loses the network". The
/// engine stops and reports the fault and an empty buffer, as a real drop does
/// after playing out what it had, and the demo streams refuse to resolve for
/// <see cref="Outage"/>. Everything after that is the real coordinator: it waits,
/// retries, and resumes the track where it stopped.
/// </summary>
public sealed class DemoNetworkDrop : IAudioPlayer, IDisposable
{
    public static readonly TimeSpan DropAt = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Outage = TimeSpan.FromSeconds(30);

    private readonly IAudioPlayer _inner;
    private readonly DemoStreamSource _streams;
    private readonly object _sync = new();
    private readonly HashSet<string> _droppedVideoIds = [];
    private string? _droppedKey;

    public DemoNetworkDrop(IAudioPlayer inner, DemoStreamSource streams)
    {
        _inner = inner;
        _streams = streams;
        _inner.EventRaised += OnInnerEvent;
    }

    public event Action<AudioPlayerEvent>? EventRaised;

    public double Volume
    {
        get => _inner.Volume;
        set => _inner.Volume = value;
    }

    public Task PlayAsync(AudioTrackRequest request, CancellationToken ct = default) =>
        _inner.PlayAsync(request, ct);

    public Task PreloadNextAsync(AudioTrackRequest request, CancellationToken ct = default) =>
        _inner.PreloadNextAsync(request, ct);

    public void ClearNext() => _inner.ClearNext();
    public void BeginCrossfade(TimeSpan duration) => _inner.BeginCrossfade(duration);
    public Task<SeekResult> SeekAsync(TimeSpan position, CancellationToken ct = default) => _inner.SeekAsync(position, ct);
    public void Pause() => _inner.Pause();
    public void Resume() => _inner.Resume();
    public void Stop() => _inner.Stop();

    private void OnInnerEvent(AudioPlayerEvent e)
    {
        if (e is ProgressEvent progress)
        {
            bool drop;
            lock (_sync)
            {
                // The dropped stream is dead: nothing more comes from it.
                if (progress.Key == _droppedKey)
                    return;
                drop = progress.Position >= DropAt && !progress.IsStarved &&
                       _droppedVideoIds.Add(VideoIdOf(progress.Key));
                if (drop)
                    _droppedKey = progress.Key;
            }

            if (drop)
            {
                _inner.Pause();
                _streams.DropNetwork(Outage);
                EventRaised?.Invoke(new PlayerFaultedEvent(
                    progress.Key, "demo network drop: read frame failed: Input/output error (-5)"));
                EventRaised?.Invoke(progress with { Buffered = TimeSpan.Zero, IsStarved = true });
                return;
            }
        }
        else if (KeyOf(e) is { } key)
        {
            lock (_sync)
            {
                if (key == _droppedKey)
                    return;
            }
        }

        EventRaised?.Invoke(e);
    }

    private static string? KeyOf(AudioPlayerEvent e) => e switch
    {
        TrackPromotedEvent p => p.Key,
        PlaybackEndedEvent p => p.Key,
        PlayerFaultedEvent p => p.Key,
        _ => null,
    };

    // The coordinator's keys are "videoId#n".
    private static string VideoIdOf(string key) =>
        key.IndexOf('#') is > 0 and var hash ? key[..hash] : key;

    public void Dispose()
    {
        _inner.EventRaised -= OnInnerEvent;
        (_inner as IDisposable)?.Dispose();
    }
}
