using Rambutan.Core.Stations;

namespace Rambutan.Core.Tests;

public class CreatedForTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static CreatedForPlaylist P(CreatedForKind kind, string id, int daysAgo, int? expiresInDays) =>
        new(kind, id, Now.AddDays(-daysAgo), expiresInDays is { } d ? Now.AddDays(d) : null);

    [Fact]
    public void TheNewestOfEachKind_ShowsInHomesOrder()
    {
        var current = CreatedFor.Current(
        [
            P(CreatedForKind.WeeklyExploration, "we", 4, 10),
            P(CreatedForKind.WeeklyJams, "wj-old", 11, 3),
            P(CreatedForKind.DailyJams, "dj", 0, 14),
            P(CreatedForKind.WeeklyJams, "wj", 4, 10),
        ], Now);

        Assert.Equal(["dj", "wj", "we"], current.Select(p => p.Id));
    }

    [Fact]
    public void AKindWhoseNewestHasExpired_HasNone_EvenWithAnOlderOne()
    {
        // ListenBrainz keeps listing weeks it stopped making.
        var current = CreatedFor.Current(
        [
            P(CreatedForKind.WeeklyJams, "new-expired", 90, -76),
            P(CreatedForKind.WeeklyJams, "older-expired", 97, -83),
            P(CreatedForKind.DailyJams, "dj", 0, 14),
        ], Now);

        Assert.Equal(["dj"], current.Select(p => p.Id));
    }

    [Fact]
    public void APlaylistWithoutAnExpiry_CountsAsCurrent()
    {
        var current = CreatedFor.Current([P(CreatedForKind.WeeklyExploration, "we", 3, null)], Now);

        Assert.Equal(["we"], current.Select(p => p.Id));
    }

    [Fact]
    public void ExpiringNow_HasExpired()
    {
        var current = CreatedFor.Current([P(CreatedForKind.DailyJams, "dj", 14, 0)], Now);

        Assert.Empty(current);
    }

    [Fact]
    public void NothingListed_ShowsNothing() => Assert.Empty(CreatedFor.Current([], Now));
}
