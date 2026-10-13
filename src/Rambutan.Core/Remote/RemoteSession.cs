using System.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.Core.Remote;

/// <summary>
/// The player as remote clients see and drive it: keeps a <see cref="RemoteState"/>
/// from the coordinator's events, and turns <see cref="RemoteCommand"/>s into
/// coordinator calls.
///
/// The state is built from the events' own arguments, never by calling back into
/// the coordinator, because its events may fire under its lock. Progress arrives
/// many times a second; a client advances the position itself while playing, so
/// progress is only published when the real position leaves that estimate (a
/// seek, a stall) or every <see cref="ResyncInterval"/> to correct drift.
///
/// Commands run through <c>dispatch</c>, which the app points at its UI thread,
/// so a phone's command takes the same path as the tray's or the media keys'.
/// </summary>
public sealed partial class RemoteSession : IRemotePlayer, IDisposable
{
    /// <summary>How far the real position may drift from a client's estimate
    /// before a new state is published.</summary>
    public static readonly TimeSpan DriftTolerance = TimeSpan.FromSeconds(1);

    /// <summary>The longest a client goes without a position while playing.</summary>
    public static readonly TimeSpan ResyncInterval = TimeSpan.FromSeconds(5);

    private readonly PlaybackCoordinator _coordinator;
    private readonly Action<Action> _dispatch;
    private readonly Func<TrackInfo, bool> _hasArtwork;
    private readonly Func<TimeSpan> _clock;
    private readonly object _sync = new();

    private PlaybackState _playback;
    private TrackInfo? _track;
    private TimeSpan _position;
    private TimeSpan? _duration;
    private double _volume;
    private bool _waiting;
    private long _queueVersion;

    private RemoteState _current;
    private TimeSpan _publishedAt;

    /// <param name="dispatch">Where commands run; the app passes its UI thread.</param>
    /// <param name="clock">Monotonic time, for the progress throttle; tests pass their own.</param>
    /// <param name="searchSongs">The catalog's song search, a page at a time
    /// (<see cref="RemoteSongPage.SearcherFor"/>); without it, every search fails.</param>
    public RemoteSession(
        PlaybackCoordinator coordinator, Action<Action> dispatch,
        Func<TrackInfo, bool>? hasArtwork = null, Func<TimeSpan>? clock = null,
        SongSearch? searchSongs = null)
    {
        _coordinator = coordinator;
        _searchSongs = searchSongs;
        _dispatch = dispatch;
        _hasArtwork = hasArtwork ?? (_ => false);
        var watch = Stopwatch.StartNew();
        _clock = clock ?? (() => watch.Elapsed);

        _playback = coordinator.State;
        _track = coordinator.NowPlaying;
        _duration = _track?.Duration;
        _volume = coordinator.Volume;
        _waiting = coordinator.TrackWaitingForNetwork is not null;
        _current = Build();
        _publishedAt = _clock();

        coordinator.StateChanged += OnStateChanged;
        coordinator.NowPlayingChanged += OnNowPlayingChanged;
        coordinator.ProgressChanged += OnProgress;
        coordinator.VolumeChanged += OnVolumeChanged;
        coordinator.NetworkWaitChanged += OnNetworkWaitChanged;
        coordinator.Queue.Changed += OnQueueChanged;
    }

    public RemoteState Current
    {
        get { lock (_sync) return _current; }
    }

    public event Action? Changed;

    public void Execute(RemoteCommand command) => _dispatch(() =>
    {
        switch (command)
        {
            case RemoteCommand.Play:
                // As the player bar's play: resume a pause, or start the queue's
                // current track again when playback stopped with tracks queued.
                if (_coordinator.State is PlaybackState.Paused)
                    _coordinator.Resume();
                else if (_coordinator.State is PlaybackState.Stopped && _coordinator.Queue.CurrentIndex >= 0)
                    _ = _coordinator.JumpToAsync(_coordinator.Queue.CurrentIndex);
                break;
            case RemoteCommand.Pause:
                _coordinator.Pause();
                break;
            case RemoteCommand.Next:
                _ = _coordinator.NextAsync();
                break;
            case RemoteCommand.Previous:
                _ = _coordinator.PreviousAsync();
                break;
            case RemoteCommand.Seek seek:
                _ = _coordinator.SeekAsync(seek.Position < TimeSpan.Zero ? TimeSpan.Zero : seek.Position);
                break;
            case RemoteCommand.SetVolume volume:
                _coordinator.Volume = volume.Volume;
                break;
        }
    });

    private void OnStateChanged(PlaybackState state) => Update(() => _playback = state);

    private void OnNowPlayingChanged(TrackInfo? track) => Update(() =>
    {
        _track = track;
        _position = TimeSpan.Zero;
        _duration = track?.Duration;
    });

    private void OnVolumeChanged(double volume) => Update(() => _volume = volume);

    private void OnNetworkWaitChanged(TrackInfo? track) => Update(() => _waiting = track is not null);

    private void OnProgress(PlaybackProgress progress)
    {
        lock (_sync)
        {
            _position = progress.Position;
            var durationChanged = progress.Duration is { } d && d != _duration;
            if (progress.Duration is not null)
                _duration = progress.Duration;

            var now = _clock();
            var sincePublished = now - _publishedAt;
            var estimate = TimeSpan.FromMilliseconds(_current.PositionMs) +
                           (_current.Playback == "playing" ? sincePublished : TimeSpan.Zero);
            var drift = (progress.Position - estimate).Duration();

            if (!durationChanged && drift <= DriftTolerance && sincePublished < ResyncInterval)
                return;
            Publish(now);
        }
        Changed?.Invoke();
    }

    private void Update(Action apply)
    {
        lock (_sync)
        {
            apply();
            var next = Build();
            if (next == _current)
                return;
            Publish(_clock());
        }
        Changed?.Invoke();
    }

    /// <summary>Takes the state as it stands. Call under <see cref="_sync"/>.</summary>
    private void Publish(TimeSpan now)
    {
        _current = Build();
        _publishedAt = now;
    }

    private RemoteState Build() => new(
        _playback.ToString().ToLowerInvariant(),
        _track is null ? null : new RemoteTrack(
            _track.VideoId, _track.Title, _track.ArtistDisplay, _track.Album,
            Milliseconds(_track.Duration), _hasArtwork(_track)),
        (long)_position.TotalMilliseconds,
        Milliseconds(_duration),
        Math.Round(_volume, 3),
        _waiting,
        _queueVersion);

    private static long? Milliseconds(TimeSpan? value) =>
        value is { } v ? (long)v.TotalMilliseconds : null;

    public void Dispose()
    {
        _coordinator.StateChanged -= OnStateChanged;
        _coordinator.NowPlayingChanged -= OnNowPlayingChanged;
        _coordinator.ProgressChanged -= OnProgress;
        _coordinator.VolumeChanged -= OnVolumeChanged;
        _coordinator.NetworkWaitChanged -= OnNetworkWaitChanged;
        _coordinator.Queue.Changed -= OnQueueChanged;
    }
}
