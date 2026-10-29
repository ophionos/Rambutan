using Rambutan.Core.Playback;

namespace Rambutan.Core.Tests;

public class TrackNotificationRuleTests
{
    private static TrackStart Start(string id, bool onItsOwn = false) => new(TestTracks.Track(id), onItsOwn);

    [Fact]
    public void Off_ShowsNothing()
    {
        var rule = new TrackNotificationRule();

        Assert.False(rule.ShouldShow(Start("t1", onItsOwn: true), enabled: false, alsoOnItsOwnWhileFocused: true, appHasFocus: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithoutTheFocus_EveryNewTrackShows(bool onItsOwn)
    {
        var rule = new TrackNotificationRule();

        Assert.True(rule.ShouldShow(Start("t1", onItsOwn), enabled: true, alsoOnItsOwnWhileFocused: false, appHasFocus: false));
    }

    [Fact]
    public void WithTheFocus_ASongTheUserPicked_NeverShows()
    {
        var rule = new TrackNotificationRule();

        Assert.False(rule.ShouldShow(Start("t1"), enabled: true, alsoOnItsOwnWhileFocused: true, appHasFocus: true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void WithTheFocus_ASongThatStartsOnItsOwn_ShowsOnlyWithTheSecondSetting(bool also, bool shows)
    {
        var rule = new TrackNotificationRule();

        Assert.Equal(shows, rule.ShouldShow(Start("t1", onItsOwn: true), enabled: true, alsoOnItsOwnWhileFocused: also, appHasFocus: true));
    }

    [Fact]
    public void TheSameTrackStartingAgain_ShowsNothingNew()
    {
        var rule = new TrackNotificationRule();
        rule.ShouldShow(Start("t1"), enabled: true, alsoOnItsOwnWhileFocused: false, appHasFocus: false);

        Assert.False(rule.ShouldShow(Start("t1", onItsOwn: true), enabled: true, alsoOnItsOwnWhileFocused: false, appHasFocus: false));
        Assert.True(rule.ShouldShow(Start("t2"), enabled: true, alsoOnItsOwnWhileFocused: false, appHasFocus: false));
    }

    [Fact]
    public void ATrackSeenWhileOff_StillCountsAsSeen()
    {
        var rule = new TrackNotificationRule();
        rule.ShouldShow(Start("t1"), enabled: false, alsoOnItsOwnWhileFocused: false, appHasFocus: false);

        Assert.False(rule.ShouldShow(Start("t1"), enabled: true, alsoOnItsOwnWhileFocused: false, appHasFocus: false));
    }

    [Fact]
    public void AfterAStop_TheSameTrackShowsAgain()
    {
        var rule = new TrackNotificationRule();
        rule.ShouldShow(Start("t1"), enabled: true, alsoOnItsOwnWhileFocused: false, appHasFocus: false);

        rule.Reset();

        Assert.True(rule.ShouldShow(Start("t1"), enabled: true, alsoOnItsOwnWhileFocused: false, appHasFocus: false));
    }
}
