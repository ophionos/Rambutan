using Rambutan.App.Services;
using Rambutan.Core.Playback;
using Rambutan.Integrations.LastFm;

namespace Rambutan.App.Tests;

/// <summary>
/// Core and the integrations raise codes; the app owns the words.
/// A code without text would fall back to its name, so every one is checked.
/// </summary>
public class NoticeTextTests
{
    public static TheoryData<PlaybackNoticeKind> PlaybackKinds() => [.. Enum.GetValues<PlaybackNoticeKind>()];

    public static TheoryData<LastFmNotice> LastFmNotices() => [.. Enum.GetValues<LastFmNotice>()];

    [Theory]
    [MemberData(nameof(PlaybackKinds))]
    public void EveryPlaybackCode_HasText(PlaybackNoticeKind kind)
    {
        var notice = new PlaybackNotice(kind, "Get Lucky", "boom");
        var text = NoticeText.For(notice);

        Assert.NotEqual(notice.ToString(), text);
        Assert.DoesNotContain("toast.", text);
        Assert.DoesNotContain("{", text);
    }

    [Theory]
    [MemberData(nameof(LastFmNotices))]
    public void EveryLastFmCode_HasText(LastFmNotice notice)
    {
        var text = NoticeText.For(notice);

        Assert.NotEqual(notice.ToString(), text);
        Assert.DoesNotContain("settings.", text);
    }

    [Fact]
    public void Details_AreInsertedAsTheyAre()
    {
        Assert.Equal("Couldn't play “Get Lucky”: 403 Forbidden",
            NoticeText.For(new PlaybackNotice(PlaybackNoticeKind.TrackFailed, "Get Lucky", "403 Forbidden")));
        Assert.Equal("Playback of “Get Lucky” failed (no formats); skipping.",
            NoticeText.For(new PlaybackNotice(PlaybackNoticeKind.TrackSkipped, "Get Lucky", "no formats")));
        Assert.Equal("Audio output: Speakers (Realtek)",
            NoticeText.For(new PlaybackNotice(PlaybackNoticeKind.OutputChanged, "Speakers (Realtek)")));
        Assert.Equal("Playback stopped after repeated failures.",
            NoticeText.For(new PlaybackNotice(PlaybackNoticeKind.StoppedAfterFailures)));
    }
}
