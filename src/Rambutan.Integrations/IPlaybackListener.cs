using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.Integrations;

/// <summary>
/// An integration that follows playback (Discord, Last.fm). The App's
/// <c>PlaybackListenerHub</c> subscribes the coordinator once and fans out to
/// every listener, so each integration only translates events into its own
/// protocol.
/// </summary>
/// <remarks>
/// Calls arrive on whatever thread the coordinator raised the event on, one at
/// a time per listener, and must not block: do network work on your own task.
/// An exception is logged by the hub and doesn't reach other listeners.
/// </remarks>
public interface IPlaybackListener
{
    /// <summary>A new track started loading, or playback ended (null).</summary>
    void OnNowPlayingChanged(TrackInfo? track);

    void OnStateChanged(PlaybackState state);

    /// <summary>
    /// Playback position, throttled to about once a second, plus immediately
    /// after a seek (a jump the elapsed time doesn't explain).
    /// </summary>
    void OnProgress(TimeSpan position, TimeSpan? duration);
}
