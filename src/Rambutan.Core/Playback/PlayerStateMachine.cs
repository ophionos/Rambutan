namespace Rambutan.Core.Playback;

public enum PlaybackState
{
    Stopped,
    Loading,
    Playing,
    Paused,
    Buffering,
    Faulted,
}

/// <summary>
/// Playback state with an explicit transition table. Invalid transitions throw —
/// a coordinator bug should fail a unit test, not corrupt UI state.
/// Not thread-safe: the coordinator serializes every check-and-transition under
/// its own lock so concurrent callers can't interleave into an invalid pair.
/// </summary>
public sealed class PlayerStateMachine
{
    private static readonly Dictionary<PlaybackState, PlaybackState[]> Allowed = new()
    {
        [PlaybackState.Stopped] = [PlaybackState.Loading],
        [PlaybackState.Loading] = [PlaybackState.Playing, PlaybackState.Faulted],
        [PlaybackState.Playing] = [PlaybackState.Paused, PlaybackState.Buffering, PlaybackState.Faulted],
        [PlaybackState.Paused] = [PlaybackState.Playing, PlaybackState.Faulted],
        [PlaybackState.Buffering] = [PlaybackState.Playing, PlaybackState.Paused, PlaybackState.Faulted],
        [PlaybackState.Faulted] = [PlaybackState.Loading],
    };

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;

    public event Action<PlaybackState>? StateChanged;

    public bool TransitionTo(PlaybackState target)
    {
        if (State == target)
            return false;

        // Two universal transitions: any state can be stopped, and any state can
        // start loading a new track (every explicit play command goes via Loading).
        var valid = target is PlaybackState.Stopped or PlaybackState.Loading
                    || Allowed[State].Contains(target);
        if (!valid)
            throw new InvalidOperationException($"Invalid playback transition {State} -> {target}.");

        State = target;
        StateChanged?.Invoke(target);
        return true;
    }
}
