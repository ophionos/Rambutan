namespace Rambutan.Core.Playback;

/// <summary>
/// When a track change shows a system notification. The OS media overlay
/// already shows the track, so a notification is for when the app is out of
/// sight: never for a song the user just picked in the app, and once per
/// track, so a track that starts again (repeat one, a resume after a network
/// fault, a seek that reopens the stream) shows nothing new.
/// Not thread-safe: the caller calls it from one thread.
/// </summary>
public sealed class TrackNotificationRule
{
    private string? _lastId;

    /// <param name="enabled">The setting, off by default.</param>
    /// <param name="alsoOnItsOwnWhileFocused">The second setting: a track
    /// that starts on its own shows one even while the app has the focus.</param>
    /// <param name="appHasFocus">An app window is the active window.</param>
    public bool ShouldShow(TrackStart start, bool enabled, bool alsoOnItsOwnWhileFocused, bool appHasFocus)
    {
        var id = start.Track.VideoId;
        var repeat = id == _lastId;
        _lastId = id;
        if (!enabled || repeat)
            return false;
        return !appHasFocus || (start.OnItsOwn && alsoOnItsOwnWhileFocused);
    }

    /// <summary>Playback stopped: the next start shows, even of the same track.</summary>
    public void Reset() => _lastId = null;
}
