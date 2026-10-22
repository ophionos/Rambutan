using Rambutan.App.Localization;
using Rambutan.Core.Playback;
using Rambutan.Integrations.LastFm;

namespace Rambutan.App.Services;

/// <summary>
/// Turns the codes that Core and the integrations raise into the user's
/// language. The libraries stay free of display text; the
/// details they carry (a track title, an exception message, a device name)
/// are inserted as they are, untranslated. Keys are literal so the drift test
/// sees them.
/// </summary>
public static class NoticeText
{
    public static string For(PlaybackNotice notice)
    {
        var title = ("title", (object?)notice.Subject);
        var detail = ("detail", (object?)notice.Detail);
        return notice.Kind switch
        {
            PlaybackNoticeKind.CommandFailed => Strings.Format("toast.playback.commandFailed", detail),
            PlaybackNoticeKind.TrackFailed => Strings.Format("toast.playback.trackFailed", title, detail),
            PlaybackNoticeKind.StoppedAfterFailures => Strings.Get("toast.playback.stoppedAfterFailures"),
            PlaybackNoticeKind.TrackSkipped => Strings.Format("toast.playback.trackSkipped", title, detail),
            PlaybackNoticeKind.OutputChanged => Strings.Format("toast.playback.outputChanged", ("device", notice.Subject)),
            PlaybackNoticeKind.OutputLost => Strings.Get("toast.playback.outputLost"),
            PlaybackNoticeKind.AutoplayFailed => Strings.Format("toast.playback.autoplayFailed", title, detail),
            PlaybackNoticeKind.AutoplayEmpty => Strings.Format("toast.playback.autoplayEmpty", title),
            PlaybackNoticeKind.AutoplayNoRadio => Strings.Format("toast.playback.autoplayNoRadio", title),
            PlaybackNoticeKind.SkippedOffline => Strings.Format("toast.playback.skippedOffline", title),
            PlaybackNoticeKind.StoppedOffline => Strings.Format("toast.playback.stoppedOffline", title),
            // A code added in Core without text here still shows something.
            _ => notice.ToString(),
        };
    }

    public static string For(LastFmNotice notice) => notice switch
    {
        LastFmNotice.NotApprovedYet => Strings.Get("settings.lastfm.notice.notApprovedYet"),
        LastFmNotice.ApprovalExpired => Strings.Get("settings.lastfm.notice.approvalExpired"),
        LastFmNotice.ConnectUnreachable => Strings.Get("settings.lastfm.notice.connectUnreachable"),
        LastFmNotice.ConnectRateLimited => Strings.Get("settings.lastfm.notice.connectRateLimited"),
        LastFmNotice.ConnectKeyRefused => Strings.Get("settings.lastfm.notice.connectKeyRefused"),
        LastFmNotice.ConnectRefused => Strings.Get("settings.lastfm.notice.connectRefused"),
        LastFmNotice.KeyRefused => Strings.Get("settings.lastfm.notice.keyRefused"),
        LastFmNotice.SessionEnded => Strings.Get("settings.lastfm.notice.sessionEnded"),
        LastFmNotice.SlowedDown => Strings.Get("settings.lastfm.notice.slowedDown"),
        LastFmNotice.Unreachable => Strings.Get("settings.lastfm.notice.unreachable"),
        _ => notice.ToString(),
    };
}
