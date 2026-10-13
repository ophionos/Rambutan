using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Providers;

namespace Rambutan.Lyrics.Tests;

public class LyricsProviderChainTests
{
    private static readonly LyricsQuery Query = new("vid", "Title", "Artist");

    private static LyricsResult Lyrics(string source) =>
        LyricsResult.Found(LyricsDocument.FromPlainText("words", source));

    [Fact]
    public async Task First_answer_wins_and_later_providers_are_not_asked()
    {
        var first = ScriptedProvider.Returning("a", Lyrics("a"));
        var second = ScriptedProvider.Returning("b", Lyrics("b"));

        var lookup = await new LyricsProviderChain([first, second]).LookupAsync(Query, CancellationToken.None);

        Assert.Equal(LyricsStatus.Found, lookup.Status);
        Assert.Equal("a", lookup.Document!.Source);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task A_miss_moves_on_to_the_next_provider()
    {
        var lookup = await new LyricsProviderChain(
            [ScriptedProvider.Returning("a", null), ScriptedProvider.Returning("b", Lyrics("b"))])
            .LookupAsync(Query, CancellationToken.None);

        Assert.Equal("b", lookup.Document!.Source);
    }

    [Fact]
    public async Task Instrumental_stops_the_chain()
    {
        var second = ScriptedProvider.Returning("b", Lyrics("b"));

        var lookup = await new LyricsProviderChain(
            [ScriptedProvider.Returning("a", LyricsResult.Instrumental("a")), second])
            .LookupAsync(Query, CancellationToken.None);

        Assert.Equal(LyricsStatus.Instrumental, lookup.Status);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task Every_provider_missing_is_not_found()
    {
        var lookup = await new LyricsProviderChain(
            [ScriptedProvider.Returning("a", null), ScriptedProvider.Returning("b", null)])
            .LookupAsync(Query, CancellationToken.None);

        Assert.Equal(LyricsStatus.NotFound, lookup.Status);
    }

    [Fact]
    public async Task A_failure_is_skipped_and_a_later_answer_still_wins()
    {
        var lookup = await new LyricsProviderChain(
            [ScriptedProvider.Throwing("a"), ScriptedProvider.Returning("b", Lyrics("b"))])
            .LookupAsync(Query, CancellationToken.None);

        Assert.Equal(LyricsStatus.Found, lookup.Status);
    }

    [Fact]
    public async Task A_failure_with_no_answer_is_unavailable_not_not_found()
    {
        var lookup = await new LyricsProviderChain(
            [ScriptedProvider.Throwing("a"), ScriptedProvider.Returning("b", null)])
            .LookupAsync(Query, CancellationToken.None);

        Assert.Equal(LyricsStatus.Unavailable, lookup.Status);
    }

    [Fact]
    public async Task A_slow_provider_is_cut_off_at_its_budget()
    {
        var chain = new LyricsProviderChain(
            [ScriptedProvider.Hanging("slow"), ScriptedProvider.Returning("b", Lyrics("b"))],
            TimeSpan.FromMilliseconds(50));

        var lookup = await chain.LookupAsync(Query, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("b", lookup.Document!.Source);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        var chain = new LyricsProviderChain([ScriptedProvider.Hanging("slow")], TimeSpan.FromMinutes(1));

        var lookup = chain.LookupAsync(Query, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
    }
}
