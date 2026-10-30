using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.Integrations.LastFm;

public sealed record ScrobbleCandidate(TrackInfo Track, DateTimeOffset StartedAt, TimeSpan? Duration);

/// <summary>
/// Decides when a play becomes a scrobble (Last.fm's rules): the track
/// is longer than 30 s, and the listener has heard half of it or 4 minutes,
/// whichever comes first. With no known duration only the 4-minute rule applies.
/// Pure: playback events and an injected clock in, at most one
/// <see cref="ScrobbleCandidate"/> per play out.
/// </summary>
/// <remarks>
/// Listened time is audio actually played (<see cref="ListenMeter"/>), not the
/// furthest position reached. Every <see cref="OnNowPlaying"/> is a new play, which is
/// how a repeat-one replay counts again: the coordinator announces each replay.
/// Not thread-safe; the listener hub already calls one event at a time.
/// </remarks>
public sealed class ScrobbleGate(TimeProvider clock)
{
    public static readonly TimeSpan MinimumTrackLength = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ListenCap = TimeSpan.FromMinutes(4);

    private TrackInfo? _track;
    private DateTimeOffset _startedAt;
    private TimeSpan? _duration;
    private bool _scrobbled;
    private readonly TimeProvider _clock = clock;
    private readonly ListenMeter _meter = new(clock);

    public TimeSpan Listened => _meter.Listened;

    public TrackInfo? Track => _track;

    public void OnNowPlaying(TrackInfo? track)
    {
        _track = track;
        _startedAt = _clock.GetUtcNow();
        _duration = track?.Duration;
        _scrobbled = false;
        _meter.Restart();
    }

    public void OnStateChanged(PlaybackState state) => _meter.OnStateChanged(state);

    /// <summary>Returns the scrobble exactly once per play, on the step that earns it.</summary>
    public ScrobbleCandidate? OnProgress(TimeSpan position, TimeSpan? duration)
    {
        if (_track is null)
            return null;

        if (duration is { } known && known > TimeSpan.Zero)
            _duration = known;

        _meter.OnProgress(position);

        if (_scrobbled || !Earned())
            return null;

        _scrobbled = true;
        return new ScrobbleCandidate(_track, _startedAt, _duration);
    }

    /// <summary>How much listening this play needs; null when it can't be scrobbled at all.</summary>
    public static TimeSpan? Threshold(TimeSpan? duration) => duration switch
    {
        { } d when d <= MinimumTrackLength => null,
        { } d => d / 2 < ListenCap ? d / 2 : ListenCap,
        null => ListenCap,
    };

    private bool Earned() => Threshold(_duration) is { } threshold && Listened >= threshold;
}
