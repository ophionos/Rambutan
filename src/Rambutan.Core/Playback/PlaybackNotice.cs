namespace Rambutan.Core.Playback;

public enum PlaybackNoticeKind
{
    /// <summary>A playback command failed. <see cref="PlaybackNotice.Detail"/>: why.</summary>
    CommandFailed,
    /// <summary>A track couldn't start; the queue moves on. Subject: its title; Detail: why.</summary>
    TrackFailed,
    /// <summary>Several tracks in a row failed, so playback stopped.</summary>
    StoppedAfterFailures,
    /// <summary>A playing track failed and was skipped. Subject: its title; Detail: why.</summary>
    TrackSkipped,
    /// <summary>The audio output moved to another device. Subject: its name.</summary>
    OutputChanged,
    /// <summary>No audio output device is left; playback paused.</summary>
    OutputLost,
    /// <summary>Autoplay's radio request failed. Subject: the seed's title; Detail: why.</summary>
    AutoplayFailed,
    /// <summary>Autoplay found nothing that hasn't played. Subject: the seed's title.</summary>
    AutoplayEmpty,
    /// <summary>Offline: a track that needs the network couldn't play, so playback
    /// skipped to the next track that plays offline. Subject: the first title skipped.</summary>
    SkippedOffline,
    /// <summary>Offline, and nothing from the current track on plays offline, so
    /// playback stopped on it. Subject: its title.</summary>
    StoppedOffline,
}

/// <summary>
/// A user-facing, non-modal message from the coordinator, as a code and its
/// arguments: Core has no strings for the screen, so the app words it in the
/// user's language. <see cref="Detail"/> is a reason from below (an
/// exception's message), passed through as is. <see cref="object.ToString"/>
/// stays the same in every language, for the log.
/// </summary>
public sealed record PlaybackNotice(PlaybackNoticeKind Kind, string? Subject = null, string? Detail = null);
