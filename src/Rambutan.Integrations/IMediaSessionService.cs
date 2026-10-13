using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.Integrations;

/// <summary>
/// OS media-session surface: now-playing metadata, playback state,
/// timeline, and transport buttons. Windows adapts SMTC, Linux MPRIS.
/// </summary>
public interface IMediaSessionService : IDisposable
{
    /// <summary>Wires the session up; hwnd is used where the OS ties a session to a window.</summary>
    void Initialize(nint windowHandle);

    void UpdateNowPlaying(TrackInfo? track);

    void UpdatePlaybackState(SessionPlaybackStatus status);

    void UpdateTimeline(TimeSpan position, TimeSpan? duration);

    event Action? PlayPauseRequested;
    event Action? NextRequested;
    event Action? PreviousRequested;

    // Asked for only where the OS surface tells them apart (MPRIS); SMTC sends
    // play and pause as PlayPauseRequested.

    /// <summary>Play: resume when paused, nothing when already playing.</summary>
    event Action? PlayRequested;

    /// <summary>Pause: nothing when already paused.</summary>
    event Action? PauseRequested;

    event Action? StopRequested;

    event Action<TimeSpan>? SeekRequested;

    /// <summary>Bring the main window forward.</summary>
    event Action? RaiseRequested;

    event Action? QuitRequested;

    /// <summary>The app's volume, 0 to 1, where the OS surface shows one (MPRIS).</summary>
    void UpdateVolume(double volume);

    /// <summary>Set the app's volume to this, 0 to 1 (MPRIS; Windows mixes per app itself).</summary>
    event Action<double>? VolumeRequested;

    /// <summary>The queue's shuffle and repeat modes, where the OS surface shows them (MPRIS).</summary>
    void UpdateShuffle(bool shuffle);

    void UpdateRepeat(RepeatMode repeat);

    /// <summary>Turn shuffle on or off (MPRIS).</summary>
    event Action<bool>? ShuffleRequested;

    /// <summary>Set the repeat mode (MPRIS).</summary>
    event Action<RepeatMode>? RepeatRequested;
}

/// <summary>What the OS session says playback is doing.</summary>
public enum SessionPlaybackStatus
{
    /// <summary>Nothing loaded, or stopped: Play starts the shown track from the beginning.</summary>
    Stopped,
    Paused,
    Playing,
}

/// <summary>No-op session for platforms without an adapter (macOS, should it ever build).</summary>
public sealed class NullMediaSessionService : IMediaSessionService
{
    public event Action? PlayPauseRequested { add { } remove { } }
    public event Action? NextRequested { add { } remove { } }
    public event Action? PreviousRequested { add { } remove { } }
    public event Action? PlayRequested { add { } remove { } }
    public event Action? PauseRequested { add { } remove { } }
    public event Action? StopRequested { add { } remove { } }
    public event Action<TimeSpan>? SeekRequested { add { } remove { } }
    public event Action? RaiseRequested { add { } remove { } }
    public event Action? QuitRequested { add { } remove { } }
    public event Action<double>? VolumeRequested { add { } remove { } }
    public event Action<bool>? ShuffleRequested { add { } remove { } }
    public event Action<RepeatMode>? RepeatRequested { add { } remove { } }

    public void Initialize(nint windowHandle) { }
    public void UpdateNowPlaying(TrackInfo? track) { }
    public void UpdatePlaybackState(SessionPlaybackStatus status) { }
    public void UpdateVolume(double volume) { }
    public void UpdateShuffle(bool shuffle) { }
    public void UpdateRepeat(RepeatMode repeat) { }
    public void UpdateTimeline(TimeSpan position, TimeSpan? duration) { }
    public void Dispose() { }
}
