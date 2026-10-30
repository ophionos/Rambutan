using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Parsing;
using Rambutan.Lyrics.Providers;
using Rambutan.Lyrics.Store;

namespace Rambutan.Lyrics.Tests;

public sealed class LyricsCacheTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));

    private LyricsCache Cache() => new(_dir.Path, _clock);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Round_trips_a_synced_document_with_words_ends_and_offset()
    {
        var doc = LrcParser.Parse("[offset:250]\n[00:10.00]<00:10.00>Hel<00:10.50>lo 世界 🎵\n[00:12.00]Next", "lrclib");

        Cache().Put("vid", new LyricsLookup(LyricsStatus.Found, doc));
        var back = Cache().TryGet("vid");

        Assert.NotNull(back);
        Assert.Equal(LyricsStatus.Found, back.Status);
        Assert.Equal(doc.Source, back.Document!.Source);
        Assert.Equal(doc.IsSynced, back.Document.IsSynced);
        Assert.Equal(doc.OffsetMs, back.Document.OffsetMs);
        Assert.Equal(doc.Lines.Count, back.Document.Lines.Count);
        for (var i = 0; i < doc.Lines.Count; i++)
        {
            var (a, b) = (doc.Lines[i], back.Document.Lines[i]);
            Assert.Equal((a.Text, a.Start, a.End), (b.Text, b.Start, b.End));
            Assert.Equal(a.Words, b.Words);
        }
    }

    [Fact]
    public void Instrumental_is_kept_without_expiry()
    {
        Cache().Put("vid", LyricsLookup.From(LyricsResult.Instrumental("lrclib")));
        _clock.Advance(TimeSpan.FromDays(365));

        Assert.Equal(LyricsStatus.Instrumental, Cache().TryGet("vid")?.Status);
    }

    [Fact]
    public void Not_found_expires_after_the_negative_ttl()
    {
        Cache().Put("vid", LyricsLookup.NotFound);

        _clock.Advance(LyricsCache.DefaultNegativeTtl - TimeSpan.FromMinutes(1));
        Assert.Equal(LyricsStatus.NotFound, Cache().TryGet("vid")?.Status);

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(Cache().TryGet("vid"));
    }

    [Fact]
    public void Unavailable_is_never_cached()
    {
        Cache().Put("vid", LyricsLookup.Unavailable);

        Assert.Null(Cache().TryGet("vid"));
        Assert.False(Directory.Exists(_dir.Path) && Directory.EnumerateFiles(_dir.Path).Any());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"version":3,"status":"found","document":{"source":"x","isSynced":false,"lines":[]}}""")]
    [InlineData("""{"version":1,"status":"found"}""")]
    [InlineData("""{"version":1,"status":"somethingNew"}""")]
    public void Unreadable_or_foreign_entries_are_misses(string contents)
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(Path.Combine(_dir.Path, "vid.json"), contents);

        Assert.Null(Cache().TryGet("vid"));
    }

    [Fact]
    public void A_local_file_has_its_own_entry_beside_youtube_ids()
    {
        Cache().Put("local:12", LyricsLookup.From(LyricsResult.Instrumental("lrclib")));

        Assert.Equal(LyricsStatus.Instrumental, Cache().TryGet("local:12")?.Status);
        Assert.Null(Cache().TryGet("local12"));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "local.12.json")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("con.json")]
    [InlineData("local:")]
    [InlineData("local:../x")]
    [InlineData("local:1.2")]
    public void Unsafe_ids_are_neither_read_nor_written(string videoId)
    {
        Cache().Put(videoId, LyricsLookup.NotFound);

        Assert.Null(Cache().TryGet(videoId));
        Assert.False(Directory.Exists(_dir.Path));
    }

    [Fact]
    public void Remove_deletes_the_entry()
    {
        Cache().Put("vid", LyricsLookup.NotFound);
        Cache().Remove("vid");

        Assert.Null(Cache().TryGet("vid"));
    }

    [Fact]
    public void Lyrics_found_by_version_1_are_served_but_stale()
    {
        Cache().Put("vid", new LyricsLookup(LyricsStatus.Found, LyricsDocument.FromPlainText("old", "lrclib")));
        Assert.NotNull(Cache().TryGet("vid", out var fresh));
        Assert.False(fresh);

        LyricsCacheFiles.MakeVersion1(_dir.Path, "vid");

        var lookup = Cache().TryGet("vid", out var stale);
        Assert.Equal("old", Assert.Single(lookup!.Document!.Lines).Text);
        Assert.True(stale);
    }

    [Fact]
    public void A_version_1_instrumental_answer_is_not_stale()
    {
        Cache().Put("vid", new LyricsLookup(LyricsStatus.Instrumental, LyricsDocument.Empty("lrclib")));
        LyricsCacheFiles.MakeVersion1(_dir.Path, "vid");

        Assert.Equal(LyricsStatus.Instrumental, Cache().TryGet("vid", out var stale)!.Status);
        Assert.False(stale);
    }
}

/// <summary>Rewrites a cache entry as the version before word timings.</summary>
internal static class LyricsCacheFiles
{
    public static void MakeVersion1(string cacheDirectory, string videoId)
    {
        var path = Path.Combine(cacheDirectory, videoId + ".json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(LyricsCache.CurrentVersion, (int)node["version"]!);
        node["version"] = 1;
        File.WriteAllText(path, node.ToJsonString());
    }
}

public sealed class CustomLyricsStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    private CustomLyricsStore Store() => new(_dir.Path);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Saved_text_comes_back_exactly_as_written()
    {
        var raw = new CustomLyrics("[offset:100]\n[00:01.00]Mine\r\n", 250);

        Store().Save("vid", raw);

        Assert.Equal(raw, Store().TryGetRaw("vid"));
    }

    [Fact]
    public void Stamped_text_becomes_synced_with_both_offsets_applied()
    {
        Store().Save("vid", new CustomLyrics("[offset:100]\n[00:02.00]Mine", 400));

        var doc = Store().TryGet("vid");

        Assert.NotNull(doc);
        Assert.Equal(CustomLyricsStore.SourceName, doc.Source);
        Assert.True(doc.IsSynced);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), Assert.Single(doc.Lines).Start);
        Assert.Equal(100, doc.OffsetMs);
    }

    [Fact]
    public void Unstamped_text_becomes_plain_lyrics()
    {
        Store().Save("vid", new CustomLyrics("Line one\nLine two"));

        var doc = Store().TryGet("vid");

        Assert.False(doc!.IsSynced);
        Assert.Equal(["Line one", "Line two"], doc.Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_newer_version_is_skipped_and_left_on_disk()
    {
        Directory.CreateDirectory(_dir.Path);
        var path = Path.Combine(_dir.Path, "vid.json");
        const string future = """{"version":2,"text":"future"}""";
        File.WriteAllText(path, future);

        Assert.Null(Store().TryGet("vid"));
        Assert.Equal(future, File.ReadAllText(path));
    }

    [Fact]
    public void Delete_removes_them_and_save_refuses_unsafe_ids()
    {
        Store().Save("vid", new CustomLyrics("x"));
        Store().Delete("vid");

        Assert.Null(Store().TryGet("vid"));
        Assert.Throws<ArgumentException>(() => Store().Save("../x", new CustomLyrics("x")));
    }

    [Theory]
    [InlineData("", CustomLyricsFormat.Empty)]
    [InlineData("  \n ", CustomLyricsFormat.Empty)]
    [InlineData("Just words\nNo stamps", CustomLyricsFormat.Plain)]
    [InlineData("[ti:Title]\n[ar:Artist]", CustomLyricsFormat.Plain)]
    [InlineData("[00:01.00]One\n[00:02.00]Two", CustomLyricsFormat.LineTimed)]
    [InlineData("[00:01.00]One\n[00:02.00]<00:02.00>Two <00:02.50>words", CustomLyricsFormat.WordTimed)]
    public void The_format_says_how_the_text_will_play(string text, CustomLyricsFormat expected) =>
        Assert.Equal(expected, CustomLyricsStore.FormatOf(text));
}

public sealed class LyricsServiceTests : IDisposable
{
    private static readonly LyricsQuery Query = new("vid", "Title", "Artist");

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private LyricsService Service(params ILyricsProvider[] providers) =>
        LyricsService.Create(_dir.Path, providers);

    private static LyricsResult Lyrics(string text) =>
        LyricsResult.Found(LyricsDocument.FromPlainText(text, "p"));

    [Fact]
    public async Task Custom_lyrics_win_and_providers_are_not_asked()
    {
        var provider = ScriptedProvider.Returning("p", Lyrics("provided"));
        new CustomLyricsStore(Path.Combine(_dir.Path, "lyrics", "custom")).Save("vid", new CustomLyrics("mine"));
        using var service = Service(provider);

        var lookup = await service.GetAsync(Query);

        Assert.Equal("mine", Assert.Single(lookup.Document!.Lines).Text);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task An_answer_is_cached_and_served_without_asking_again()
    {
        var provider = ScriptedProvider.Returning("p", Lyrics("provided"));
        using (var first = Service(provider))
            await first.GetAsync(Query);

        using var second = Service(provider);
        var lookup = await second.GetAsync(Query);

        Assert.Equal("provided", Assert.Single(lookup.Document!.Lines).Text);
        Assert.Equal(1, provider.Calls);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "lyrics", "cache", "vid.json")));
    }

    [Fact]
    public async Task A_miss_with_nothing_asked_is_not_cached_nor_hides_what_was_cached()
    {
        var answer = LyricsResult.NotAsked("p");
        var provider = new ScriptedProvider("p", (_, _) => Task.FromResult<LyricsResult?>(answer));
        using var service = Service(provider);

        var first = await service.GetAsync(Query);
        answer = Lyrics("matched");
        var second = await service.GetAsync(Query);

        Assert.False(first.Asked);
        Assert.Equal(LyricsStatus.NotFound, first.Status);
        Assert.Equal("matched", Assert.Single(second.Document!.Lines).Text);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task Not_found_is_cached_but_unavailable_is_asked_again()
    {
        var missing = ScriptedProvider.Returning("p", null);
        using (var service = Service(missing))
        {
            Assert.Equal(LyricsStatus.NotFound, (await service.GetAsync(Query)).Status);
            Assert.Equal(LyricsStatus.NotFound, (await service.GetAsync(Query)).Status);
        }
        Assert.Equal(1, missing.Calls);

        var failing = ScriptedProvider.Throwing("p");
        using (var service = Service(failing))
        {
            var other = Query with { VideoId = "other" };
            Assert.Equal(LyricsStatus.Unavailable, (await service.GetAsync(other)).Status);
            Assert.Equal(LyricsStatus.Unavailable, (await service.GetAsync(other)).Status);
        }
        Assert.Equal(2, failing.Calls);
    }

    [Fact]
    public async Task Lookups_off_asks_no_provider_and_caches_nothing()
    {
        var provider = ScriptedProvider.Returning("p", Lyrics("provided"));
        using var service = Service(provider);
        service.LookUpOnline = false;

        Assert.Equal(LyricsStatus.NotLookedUp, (await service.GetAsync(Query)).Status);
        Assert.Equal(0, provider.Calls);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "lyrics", "cache", "vid.json")));

        service.LookUpOnline = true;
        Assert.Equal("provided", Assert.Single((await service.GetAsync(Query)).Document!.Lines).Text);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Lookups_off_still_serves_custom_and_cached_lyrics()
    {
        var provider = ScriptedProvider.Returning("p", Lyrics("provided"));
        using (var first = Service(provider))
            await first.GetAsync(Query);
        new CustomLyricsStore(Path.Combine(_dir.Path, "lyrics", "custom")).Save("mine", new CustomLyrics("my words"));

        using var service = Service(provider);
        service.LookUpOnline = false;

        Assert.Equal("provided", Assert.Single((await service.GetAsync(Query)).Document!.Lines).Text);
        Assert.Equal("my words", Assert.Single((await service.GetAsync(Query with { VideoId = "mine" })).Document!.Lines).Text);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Switching_lookups_on_asks_for_a_track_answered_while_off()
    {
        var release = new TaskCompletionSource<LyricsResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider("p", (_, _) => release.Task);
        using var service = Service(provider);

        service.LookUpOnline = false;
        var off = await service.GetAsync(Query);
        service.LookUpOnline = true;
        var on = service.GetAsync(Query);
        release.SetResult(Lyrics("provided"));

        Assert.Equal(LyricsStatus.NotLookedUp, off.Status);
        Assert.Equal(LyricsStatus.Found, (await on).Status);
    }

    [Fact]
    public async Task Concurrent_requests_for_one_track_share_a_fetch()
    {
        var release = new TaskCompletionSource<LyricsResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider("p", (_, _) => release.Task);
        using var service = Service(provider);

        var a = service.GetAsync(Query);
        var b = service.GetAsync(Query);
        release.SetResult(Lyrics("shared"));

        Assert.Same(await a, await b);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Reload_shows_saved_lyrics_at_once_even_while_a_lookup_is_in_flight()
    {
        var release = new TaskCompletionSource<LyricsResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider("p", (_, _) => release.Task);
        using var service = Service(provider);
        var pending = service.GetAsync(Query);

        service.SaveCustom("vid", new CustomLyrics("[00:01.00]Mine", 200));
        var reloaded = await service.ReloadAsync(Query);

        Assert.Equal("custom", reloaded.Document!.Source);
        Assert.Equal(TimeSpan.FromMilliseconds(800), Assert.Single(reloaded.Document.Lines).Start);
        Assert.Equal(new CustomLyrics("[00:01.00]Mine", 200), service.GetCustom("vid"));
        release.SetResult(null);
        await pending;
    }

    [Fact]
    public async Task Removing_custom_lyrics_falls_back_to_what_the_providers_found()
    {
        var provider = ScriptedProvider.Returning("p", Lyrics("provided"));
        using var service = Service(provider);
        await service.GetAsync(Query);
        service.SaveCustom("vid", new CustomLyrics("mine"));
        Assert.Equal("mine", Assert.Single((await service.ReloadAsync(Query)).Document!.Lines).Text);

        service.RemoveCustom("vid");

        Assert.Null(service.GetCustom("vid"));
        Assert.Equal("provided", Assert.Single((await service.ReloadAsync(Query)).Document!.Lines).Text);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task A_cancelled_caller_stops_waiting_but_the_fetch_still_lands_in_the_cache()
    {
        var release = new TaskCompletionSource<LyricsResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider("p", (_, _) => release.Task);
        using var service = Service(provider);
        using var cts = new CancellationTokenSource();

        var abandoned = service.GetAsync(Query, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        // Joins the fetch still in flight rather than starting another.
        var again = service.GetAsync(Query);
        release.SetResult(Lyrics("late"));

        Assert.Equal("late", Assert.Single((await again).Document!.Lines).Text);
        Assert.Equal(1, provider.Calls);
    }

    private string CacheDirectory => Path.Combine(_dir.Path, "lyrics", "cache");

    /// <summary>"old" in the cache, as the version before word timings left it.</summary>
    private async Task CacheOldLyrics()
    {
        using (var first = Service(ScriptedProvider.Returning("p", Lyrics("old"))))
            await first.GetAsync(Query);
        LyricsCacheFiles.MakeVersion1(CacheDirectory, "vid");
    }

    [Fact]
    public async Task Lyrics_cached_before_word_timings_are_looked_up_again()
    {
        await CacheOldLyrics();
        var provider = ScriptedProvider.Returning("p", Lyrics("new"));

        using (var service = Service(provider))
            Assert.Equal("new", Assert.Single((await service.GetAsync(Query)).Document!.Lines).Text);

        // Replaced at the current version, so the next play doesn't ask again.
        using (var service = Service(provider))
            Assert.Equal("new", Assert.Single((await service.GetAsync(Query)).Document!.Lines).Text);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Stale_lyrics_are_served_as_they_are_with_lookups_off()
    {
        await CacheOldLyrics();
        var provider = ScriptedProvider.Returning("p", Lyrics("new"));
        using var service = Service(provider);
        service.LookUpOnline = false;

        Assert.Equal("old", Assert.Single((await service.GetAsync(Query)).Document!.Lines).Text);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Stale_lyrics_outlive_a_failed_lookup_and_are_asked_for_again()
    {
        await CacheOldLyrics();
        var failing = ScriptedProvider.Throwing("p");

        using (var service = Service(failing))
        {
            Assert.Equal("old", Assert.Single((await service.GetAsync(Query)).Document!.Lines).Text);
            Assert.Equal("old", Assert.Single((await service.GetAsync(Query)).Document!.Lines).Text);
        }
        Assert.Equal(2, failing.Calls);
    }

    [Fact]
    public async Task Stale_lyrics_outlive_a_miss_and_are_then_kept()
    {
        await CacheOldLyrics();
        var missing = ScriptedProvider.Returning("p", null);

        using (var service = Service(missing))
        {
            Assert.Equal("old", Assert.Single((await service.GetAsync(Query)).Document!.Lines).Text);
            Assert.Equal("old", Assert.Single((await service.GetAsync(Query)).Document!.Lines).Text);
        }
        Assert.Equal(1, missing.Calls);
    }
}
