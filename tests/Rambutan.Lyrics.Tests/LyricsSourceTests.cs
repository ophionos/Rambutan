using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Providers;
using Rambutan.Lyrics.Store;

namespace Rambutan.Lyrics.Tests;

/// <summary>
/// The user chooses which online source the lyrics come from. Only that source
/// is asked, each keeps its own cache, and a fetch for one is never joined by
/// a request for another.
/// </summary>
public sealed class LyricsSourceTests : IDisposable
{
    private static readonly LyricsQuery Query = new("khnokW3Mw24", "Title", "Artist");

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static LyricsResult Lyrics(string text, string source) =>
        LyricsResult.Found(LyricsDocument.FromPlainText(text, source));

    private LyricsService Service(ILyricsProvider first, ILyricsProvider second) =>
        LyricsService.Create(_dir.Path, [first, second]);

    [Fact]
    public async Task The_first_source_is_chosen_and_the_other_is_not_asked()
    {
        var a = ScriptedProvider.Returning("a", Lyrics("from a", "a"));
        var b = ScriptedProvider.Returning("b", Lyrics("from b", "b"));
        using var service = Service(a, b);

        Assert.Equal(["a", "b"], service.Sources);
        Assert.Equal("a", service.Source);
        Assert.Equal("from a", Assert.Single((await service.GetAsync(Query)).Document!.Lines).Text);
        Assert.Equal(0, b.Calls);
    }

    [Fact]
    public async Task A_miss_on_the_chosen_source_does_not_fall_back_to_the_other()
    {
        var a = ScriptedProvider.Returning("a", null);
        var b = ScriptedProvider.Returning("b", Lyrics("from b", "b"));
        using var service = Service(a, b);

        Assert.Equal(LyricsStatus.NotFound, (await service.GetAsync(Query)).Status);
        Assert.Equal(0, b.Calls);
    }

    [Fact]
    public async Task Switching_after_a_cached_answer_asks_the_new_source()
    {
        var a = ScriptedProvider.Returning("a", Lyrics("from a", "a"));
        var b = ScriptedProvider.Returning("b", Lyrics("from b", "b"));
        using var service = Service(a, b);
        await service.GetAsync(Query);

        service.Source = "b";

        Assert.Equal("from b", Assert.Single((await service.GetAsync(Query)).Document!.Lines).Text);
        Assert.Equal(1, b.Calls);
    }

    [Fact]
    public async Task A_cached_miss_on_one_source_does_not_hide_the_others_lyrics()
    {
        var a = ScriptedProvider.Returning("a", null);
        var b = ScriptedProvider.Returning("b", Lyrics("from b", "b"));
        using var service = Service(a, b);
        Assert.Equal(LyricsStatus.NotFound, (await service.GetAsync(Query)).Status);

        service.Source = "b";

        Assert.Equal(LyricsStatus.Found, (await service.GetAsync(Query)).Status);
    }

    [Fact]
    public async Task Switching_back_serves_each_sources_own_cache_without_asking_again()
    {
        var a = ScriptedProvider.Returning("a", Lyrics("from a", "a"));
        var b = ScriptedProvider.Returning("b", Lyrics("from b", "b"));
        using (var service = Service(a, b))
        {
            await service.GetAsync(Query);
            service.Source = "b";
            await service.GetAsync(Query);
        }

        using var again = Service(a, b);
        Assert.Equal("from a", Assert.Single((await again.GetAsync(Query)).Document!.Lines).Text);
        again.Source = "b";
        Assert.Equal("from b", Assert.Single((await again.GetAsync(Query)).Document!.Lines).Text);
        Assert.Equal((1, 1), (a.Calls, b.Calls));
        // The first source keeps the folder the cache had before there was a choice.
        Assert.True(File.Exists(Path.Combine(_dir.Path, "lyrics", "cache", "khnokW3Mw24.json")));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "lyrics", "cache", "b", "khnokW3Mw24.json")));
    }

    [Fact]
    public async Task A_request_for_another_source_does_not_join_a_fetch_in_flight()
    {
        var release = new TaskCompletionSource<LyricsResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new ScriptedProvider("a", (_, _) => release.Task);
        var b = ScriptedProvider.Returning("b", Lyrics("from b", "b"));
        using var service = Service(a, b);
        var pending = service.GetAsync(Query);

        service.Source = "b";
        var other = await service.GetAsync(Query);

        Assert.Equal("from b", Assert.Single(other.Document!.Lines).Text);
        release.SetResult(Lyrics("from a", "a"));
        Assert.Equal("from a", Assert.Single((await pending).Document!.Lines).Text);
    }

    [Fact]
    public async Task Reload_asks_the_chosen_source()
    {
        var a = ScriptedProvider.Returning("a", Lyrics("from a", "a"));
        var b = ScriptedProvider.Returning("b", Lyrics("from b", "b"));
        using var service = Service(a, b);
        service.Source = "b";

        Assert.Equal("from b", Assert.Single((await service.ReloadAsync(Query)).Document!.Lines).Text);
        Assert.Equal(0, a.Calls);
    }

    [Fact]
    public async Task Custom_lyrics_win_whichever_source_is_chosen()
    {
        var a = ScriptedProvider.Returning("a", Lyrics("from a", "a"));
        var b = ScriptedProvider.Returning("b", Lyrics("from b", "b"));
        using var service = Service(a, b);
        service.SaveCustom(Query.VideoId, new CustomLyrics("mine"));
        service.Source = "b";

        Assert.Equal("custom", (await service.GetAsync(Query)).Document!.Source);
        Assert.Equal((0, 0), (a.Calls, b.Calls));
    }

    [Fact]
    public void An_unknown_source_chooses_the_first()
    {
        using var service = Service(ScriptedProvider.Returning("a", null), ScriptedProvider.Returning("b", null));
        service.Source = "b";

        service.Source = "a source of a newer build";

        Assert.Equal("a", service.Source);
    }

    [Fact]
    public async Task The_credit_line_is_kept_in_the_cache()
    {
        var doc = LyricsDocument.FromPlainText("words", "a") with { Credit = "Source: Somewhere" };
        var a = ScriptedProvider.Returning("a", LyricsResult.Found(doc));
        using (var first = Service(a, ScriptedProvider.Returning("b", null)))
            await first.GetAsync(Query);

        using var second = Service(a, ScriptedProvider.Returning("b", null));

        Assert.Equal("Source: Somewhere", (await second.GetAsync(Query)).Document!.Credit);
        Assert.Equal(1, a.Calls);
    }
}

/// <summary>YouTube Music's lyrics come by a YouTube id only: the track's, or
/// its match's.</summary>
public sealed class YouTubeMusicLyricsProviderTests
{
    [Fact]
    public async Task The_text_is_plain_lyrics_with_the_credit_line()
    {
        var asked = new List<string>();
        var provider = new YouTubeMusicLyricsProvider((id, _) =>
        {
            asked.Add(id);
            return Task.FromResult<YouTubeMusicLyrics?>(new("One\nTwo\n\nThree\n", " Source: Musixmatch "));
        });

        var result = await provider.TryGetAsync(new LyricsQuery("khnokW3Mw24", "Title", "Artist"), CancellationToken.None);

        Assert.Equal(["khnokW3Mw24"], asked);
        var doc = result!.Document;
        Assert.Equal(YouTubeMusicLyricsProvider.SourceName, doc.Source);
        Assert.False(doc.IsSynced);
        Assert.Equal(["One", "Two", "", "Three"], doc.Lines.Select(l => l.Text));
        Assert.Equal("Source: Musixmatch", doc.Credit);
    }

    [Fact]
    public async Task Timed_lines_are_line_synced_each_lasting_until_the_next()
    {
        static TimeSpan ms(int value) => TimeSpan.FromMilliseconds(value);
        var provider = new YouTubeMusicLyricsProvider((_, _) => Task.FromResult<YouTubeMusicLyrics?>(new(
            "♪\nOne\nTwo",
            "Source: Musixmatch",
            [(ms(0), ms(26520), "♪"), (ms(30920), ms(33000), "Two"), (ms(26520), ms(29000), "One")])));

        var doc = (await provider.TryGetAsync(new LyricsQuery("khnokW3Mw24", "T", "A"), CancellationToken.None))!.Document;

        Assert.True(doc.IsSynced);
        Assert.Equal("Source: Musixmatch", doc.Credit);
        Assert.Equal(["♪", "One", "Two"], doc.Lines.Select(l => l.Text));
        Assert.Equal([ms(0), ms(26520), ms(30920)], doc.Lines.Select(l => l.Start));
        Assert.Equal([ms(26520), ms(30920), ms(33000)], doc.Lines.Select(l => l.End));
        Assert.All(doc.Lines, l => Assert.Null(l.Words));
    }

    [Theory]
    [InlineData("local:3")]
    [InlineData("")]
    public async Task A_track_that_isnt_on_YouTube_without_a_match_asks_nothing(string id)
    {
        var calls = 0;
        var askedForMatch = new List<string>();
        var provider = new YouTubeMusicLyricsProvider(
            (_, _) =>
            {
                calls++;
                return Task.FromResult<YouTubeMusicLyrics?>(new("words", null));
            },
            (local, _) =>
            {
                askedForMatch.Add(local);
                return Task.FromResult<string?>(null);
            });
        var withoutMatches = new YouTubeMusicLyricsProvider((_, _) =>
        {
            calls++;
            return Task.FromResult<YouTubeMusicLyrics?>(new("words", null));
        });

        var result = await provider.TryGetAsync(new LyricsQuery(id, "Title", "Artist"), CancellationToken.None);
        var without = await withoutMatches.TryGetAsync(new LyricsQuery(id, "Title", "Artist"), CancellationToken.None);

        Assert.True(result!.IsNotAsked);
        Assert.True(without!.IsNotAsked);
        Assert.Equal(0, calls);
        Assert.Equal(id == "" ? [] : [id], askedForMatch);
    }

    [Fact]
    public async Task A_matched_file_asks_by_its_songs_id()
    {
        var asked = new List<string>();
        var provider = new YouTubeMusicLyricsProvider(
            (id, _) =>
            {
                asked.Add(id);
                return Task.FromResult<YouTubeMusicLyrics?>(new("words", null));
            },
            (_, _) => Task.FromResult<string?>("khnokW3Mw24"));

        var result = await provider.TryGetAsync(new LyricsQuery("local:3", "Title", "Artist"), CancellationToken.None);

        Assert.Equal(["khnokW3Mw24"], asked);
        Assert.Equal("words", Assert.Single(result!.Document.Lines).Text);
    }

    [Fact]
    public async Task A_match_that_cant_be_told_is_a_failure()
    {
        var provider = new YouTubeMusicLyricsProvider(
            (_, _) => Task.FromResult<YouTubeMusicLyrics?>(new("words", null)),
            (_, _) => throw new HttpRequestException("offline"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.TryGetAsync(new LyricsQuery("local:3", "Title", "Artist"), CancellationToken.None));
    }

    [Fact]
    public async Task No_lyrics_on_YouTube_Music_is_a_miss()
    {
        var provider = new YouTubeMusicLyricsProvider((_, _) => Task.FromResult<YouTubeMusicLyrics?>(null));

        Assert.Null(await provider.TryGetAsync(new LyricsQuery("khnokW3Mw24", "T", "A"), CancellationToken.None));
    }
}
