namespace Rambutan.Core.Remote;

/// <summary>
/// What a remote client sees of the
/// player. Plain values only, so the local API can serialize it as it stands.
/// </summary>
/// <param name="Playback">"stopped", "loading", "playing", "paused", "buffering"
/// or "faulted": <see cref="Playback.PlaybackState"/> in lower case.</param>
/// <param name="PositionMs">Where the track was when this state was taken. A
/// client advances it itself while <paramref name="Playback"/> is "playing";
/// a new state arrives whenever the real position leaves that estimate.</param>
/// <param name="WaitingForNetwork">The track lost the network and waits for it.</param>
/// <param name="QueueVersion">Counts the queue's changes (its tracks, their order,
/// the current one). The queue itself isn't in the state, which goes out on
/// every progress resync; a client showing the queue asks for it again when
/// this changes.</param>
public sealed record RemoteState(
    string Playback,
    RemoteTrack? Track,
    long PositionMs,
    long? DurationMs,
    double Volume,
    bool WaitingForNetwork,
    long QueueVersion = 0)
{
    public static readonly RemoteState Empty = new("stopped", null, 0, null, 1.0, false);
}

/// <param name="Artists">The credits joined for display ("A, B").</param>
/// <param name="HasArtwork">The host can serve this track's cover.</param>
public sealed record RemoteTrack(
    string VideoId,
    string Title,
    string Artists,
    string? Album,
    long? DurationMs,
    bool HasArtwork);

/// <summary>What a remote client may ask the player to do. Play and pause are
/// separate commands rather than a toggle, so two phones, or one showing a stale
/// state, can't invert what the user meant.</summary>
public abstract record RemoteCommand
{
    public sealed record Play : RemoteCommand;
    public sealed record Pause : RemoteCommand;
    public sealed record Next : RemoteCommand;
    public sealed record Previous : RemoteCommand;
    public sealed record Seek(TimeSpan Position) : RemoteCommand;

    /// <param name="Volume">0..1; out-of-range values are clamped.</param>
    public sealed record SetVolume(double Volume) : RemoteCommand;
}

/// <summary>
/// The player as a remote client reaches it. <see cref="RemoteSession"/> is the
/// real one; the local API depends on this so its tests can stand in for it.
/// </summary>
public interface IRemotePlayer
{
    RemoteState Current { get; }

    /// <summary>Raised when <see cref="Current"/> changes in a way a client should
    /// hear about. May fire on any thread, including under the coordinator's lock:
    /// handlers only signal, and read <see cref="Current"/> later.</summary>
    event Action? Changed;

    void Execute(RemoteCommand command);
}
