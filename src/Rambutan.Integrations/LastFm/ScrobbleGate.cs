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
/// Listened time is audio actually played, not the furthest position reached:
/// a position step counts only while playing, only forwards, and only as far
/// as the wall clock could explain, so a seek (either way) adds nothing and a
/// pause adds nothing. Every <see cref="OnNowPlaying"/> is a new play, which is
/// how a repeat-one replay counts again: the coordinator announces each replay.
/// Not thread-safe; the listener hub already calls one event at a time.
/// </remarks>
public sealed class ScrobbleGate(TimeProvider clock)
{
    public static readonly TimeSpan MinimumTrackLength = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ListenCap = TimeSpan.FromMinutes(4);

    /// <summary>How far a position step may outrun the wall clock and still count as playback.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    private TrackInfo? _track;
    private DateTimeOffset _startedAt;
    private TimeSpan? _duration;
    private bool _scrobbled;
    private bool _playing;

    private bool _hasBaseline;
    private TimeSpan _lastPosition;
    private DateTimeOffset _lastAt;

    public TimeSpan Listened { get; private set; }

    public TrackInfo? Track => _track;

    public void OnNowPlaying(TrackInfo? track)
    {
        _track = track;
        _startedAt = clock.GetUtcNow();
        _duration = track?.Duration;
        _scrobbled = false;
        _hasBaseline = false;
        Listened = TimeSpan.Zero;
    }

    public void OnStateChanged(PlaybackState state)
    {
        var playing = state == PlaybackState.Playing;
        // Whatever happened while not playing (a pause, a stall, a seek landing
        // during buffering) must not be bridged by the next step.
        if (!playing)
            _hasBaseline = false;
        _playing = playing;
    }

    /// <summary>Returns the scrobble exactly once per play, on the step that earns it.</summary>
    public ScrobbleCandidate? OnProgress(TimeSpan position, TimeSpan? duration)
    {
        if (_track is null)
            return null;

        if (duration is { } known && known > TimeSpan.Zero)
            _duration = known;

        var now = clock.GetUtcNow();
        if (_playing && _hasBaseline)
        {
            var step = position - _lastPosition;
            var wall = now - _lastAt;
            if (step > TimeSpan.Zero && step <= wall + Slack)
                Listened += step;
        }

        _hasBaseline = _playing;
        _lastPosition = position;
        _lastAt = now;

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
