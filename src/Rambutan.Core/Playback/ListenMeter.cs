namespace Rambutan.Core.Playback;

/// <summary>
/// How much of a play was actually heard, from the playback events the
/// integrations see: a position step counts only while playing, only forwards,
/// and only as far as the wall clock could explain, so a seek (either way) adds
/// nothing and a pause adds nothing. Last.fm's scrobble rules and the play log
/// both count from it.
/// </summary>
/// <remarks>Not thread-safe; the listener hub calls one event at a time.</remarks>
public sealed class ListenMeter(TimeProvider clock)
{
    /// <summary>How far a position step may outrun the wall clock and still count as playback.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    private bool _playing;
    private bool _hasBaseline;
    private TimeSpan _lastPosition;
    private DateTimeOffset _lastAt;

    public TimeSpan Listened { get; private set; }

    /// <summary>Starts a new play from nothing heard; whether playback runs is kept.</summary>
    public void Restart()
    {
        _hasBaseline = false;
        Listened = TimeSpan.Zero;
    }

    /// <summary>Counts from nothing again within the same track: the next step
    /// still counts, since the position goes on from where it was.</summary>
    public void ResetListened() => Listened = TimeSpan.Zero;

    public void OnStateChanged(PlaybackState state)
    {
        var playing = state == PlaybackState.Playing;
        // Whatever happened while not playing (a pause, a stall, a seek landing
        // during buffering) must not be bridged by the next step.
        if (!playing)
            _hasBaseline = false;
        _playing = playing;
    }

    public void OnProgress(TimeSpan position)
    {
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
    }
}
