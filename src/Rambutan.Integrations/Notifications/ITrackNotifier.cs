namespace Rambutan.Integrations.Notifications;

/// <summary>What a track notification says.</summary>
/// <param name="CoverFile">A cover image on this computer, or null: never a
/// URL, and never the music file's own path.</param>
public sealed record TrackNotification(string Title, string Body, string? CoverFile);

/// <summary>
/// The system notification for a track change: a toast on Windows, the
/// desktop's notification daemon on Linux. Each one replaces the last, so a
/// run of skips leaves one notification. The system's do-not-disturb state
/// applies on its own. A failure is logged, never shown: the notification is
/// a convenience, and the media overlay still shows the track.
/// </summary>
/// <remarks>Called from the UI thread; must not block.</remarks>
public interface ITrackNotifier : IDisposable
{
    /// <summary>False where this build can never show one (Windows: not an
    /// installed build, so there is no Start menu entry for the toasts to
    /// come from). The setting then says why it does nothing.</summary>
    bool CanShow { get; }

    void Show(TrackNotification notification);

    /// <summary>Takes the shown notification away, if any.</summary>
    void Close();
}

/// <summary>For platforms without notifications here (macOS, should it ever build).</summary>
public sealed class NullTrackNotifier : ITrackNotifier
{
    public bool CanShow => false;
    public void Show(TrackNotification notification) { }
    public void Close() { }
    public void Dispose() { }
}
