using System.Net;
using Rambutan.Core.Models;
using Rambutan.Lyrics.Providers;

namespace Rambutan.Lyrics.Tests;

public class LrcLibProviderTests
{
    private static readonly LyricsQuery Full = new(
        "abcDEF12_-3", "Paper Lanterns", "The Quiet Harbour", "Tidelines", TimeSpan.FromSeconds(212.4));

    // The tracks the recorded fixtures answer for (public-domain songs, so the
    // recorded lyrics can stay).
    private static readonly LyricsQuery AmazingGrace = new(
        "abcDEF12_-3", "Amazing Grace", "Melvin West", "Great Hymns of the Church", TimeSpan.FromSeconds(277));

    private static readonly LyricsQuery Greensleeves = new(
        "abcDEF12_-3", "Greensleeves", "Dublin Ramblers", "Whiskey In The Jar From Ireland", TimeSpan.FromSeconds(97));

    private static (LrcLibProvider Provider, FakeLrcLibHandler Handler) Create(
        Action<FakeLrcLibHandler> setup, InstantDelays? time = null)
    {
        var handler = new FakeLrcLibHandler();
        setup(handler);
        return (new LrcLibProvider(new HttpClient(handler), time ?? new InstantDelays()), handler);
    }

    /// <summary>Answers with each status in turn, then with the last one, as LRCLIB
    /// does when it is busy; a 200 serves the recorded exact match.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> InTurn(params HttpStatusCode[] statuses)
    {
        var next = 0;
        return _ =>
        {
            var status = statuses[Math.Min(next++, statuses.Length - 1)];
            return status == HttpStatusCode.OK
                ? Json(Fixtures.Read("lrclib-get.json"))
                : new HttpResponseMessage(status) { Content = new StringContent(Fixtures.Read("lrclib-overloaded.json")) };
        };
    }

    private static Dictionary<string, string> QueryOf(HttpRequestMessage request) =>
        request.RequestUri!.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public async Task Exact_match_returns_synced_lyrics_parsed_end_to_end()
    {
        var (provider, handler) = Create(h => h.Serve("get", "lrclib-get.json"));

        var result = await provider.TryGetAsync(AmazingGrace, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsInstrumental);
        var doc = result.Document;
        Assert.Equal("lrclib", doc.Source);
        Assert.True(doc.IsSynced);
        Assert.Equal(["Amazing grace", "How sweet the sound", "That would save"], doc.Lines.Take(3).Select(l => l.Text));
        Assert.Equal(new TimeSpan(0, 0, 0, 0, 210), doc.Lines[0].Start);
        Assert.Equal(doc.Lines[1].Start, doc.Lines[0].End);
        Assert.Equal("I was blind but now I see", doc.Lines[^2].Text);
        Assert.Equal(new TimeSpan(0, 0, 4, 37, 130), doc.Lines[^1].Start);
        Assert.Equal(["/api/get"], handler.Endpoints);
    }

    [Fact]
    public async Task Exact_match_sends_all_four_fields_and_identifies_itself()
    {
        var (provider, handler) = Create(h => h.Serve("get", "lrclib-get.json"));

        await provider.TryGetAsync(Full with { Title = "Rock & Roll / Part 2?" }, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        var query = QueryOf(request);
        Assert.Equal("Rock & Roll / Part 2?", query["track_name"]);
        Assert.Equal("The Quiet Harbour", query["artist_name"]);
        Assert.Equal("Tidelines", query["album_name"]);
        Assert.Equal("212", query["duration"]);
        var agent = request.Headers.UserAgent.ToString();
        Assert.StartsWith("Rambutan/", agent);
        Assert.Contains("github.com/ophionos/Rambutan", agent);
    }

    [Fact]
    public async Task Plain_only_record_returns_unsynced_lyrics()
    {
        var (provider, _) = Create(h => h.Serve("get", "lrclib-get.plain-only.json"));

        var result = await provider.TryGetAsync(Greensleeves, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Document.IsSynced);
        Assert.Equal(
            ["Alas, my love, you do me wrong", "To cast me off discourteously", "And I have loved you so long"],
            result.Document.Lines.Take(3).Select(l => l.Text));
    }

    [Fact]
    public async Task Instrumental_flag_is_an_answer_not_a_miss()
    {
        var (provider, handler) = Create(h => h.Serve("get", "lrclib-get.instrumental.json"));

        var result = await provider.TryGetAsync(
            new LyricsQuery("abcDEF12_-3", "Greensleeves", "the mantovani orchestra", "Beautiful Moments", TimeSpan.FromSeconds(189)),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsInstrumental);
        Assert.Empty(result.Document.Lines);
        Assert.Equal(["/api/get"], handler.Endpoints);
    }

    [Fact]
    public async Task Non_latin_text_and_emoji_survive_the_round_trip()
    {
        var (provider, _) = Create(h => h.Serve("get", _ => Json("""
            {"id":1,"trackName":"t","artistName":"a","albumName":"b","duration":212,"instrumental":false,
             "plainLyrics":"x","syncedLyrics":"[00:01.00] 夜の灯り、ゆらゆら\n[00:02.00] Float until the light is gone 🌙"}
            """)));

        var result = await provider.TryGetAsync(Full, CancellationToken.None);

        Assert.Equal(["夜の灯り、ゆらゆら", "Float until the light is gone 🌙"], result!.Document.Lines.Select(l => l.Text));
    }

    [Fact]
    public async Task Synced_field_without_stamps_falls_back_to_plain()
    {
        var (provider, _) = Create(h => h.Serve("get", _ => Json("""
            {"id":1,"trackName":"t","artistName":"a","albumName":"b","duration":212,"instrumental":false,
             "plainLyrics":"Just words","syncedLyrics":"no stamps here"}
            """)));

        var result = await provider.TryGetAsync(Full, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Document.IsSynced);
        Assert.Equal("Just words", Assert.Single(result.Document.Lines).Text);
    }

    [Fact]
    public async Task Word_synced_record_returns_its_word_timings()
    {
        var (provider, _) = Create(h => h.Serve("get", "lrclib-get.word-synced.synthetic.json"));

        var result = await provider.TryGetAsync(Full, CancellationToken.None);

        Assert.NotNull(result);
        var doc = result.Document;
        Assert.Equal("lrclib", doc.Source);
        Assert.True(doc.IsSynced);
        Assert.All(doc.Lines, l => Assert.NotNull(l.Words));
        Assert.Equal(["Paper ", "lanterns ", "on ", "the ", "water"], doc.Lines[0].Words!.Select(w => w.Text));
        Assert.Equal(new TimeSpan(0, 0, 0, 17, 600), doc.Lines[0].Words![1].Start);
    }

    [Theory]
    // Word sync claimed, but the Lyricsfile is broken, of an unknown version, or
    // has no words after all: the record's LRC is still good.
    [InlineData(true, @"version: '1.0'\nlines: [broken")]
    [InlineData(true, @"version: '9.9'\nmetadata:\n  title: t\n  artist: a\nlines:\n  - text: w\n    start_ms: 1000\n    words:\n      - text: w\n        start_ms: 1000")]
    [InlineData(true, @"version: '1.0'\nmetadata:\n  title: t\n  artist: a\nlines:\n  - text: w\n    start_ms: 1000")]
    // Words present, but the record doesn't claim word sync: not trusted.
    [InlineData(false, @"version: '1.0'\nmetadata:\n  title: t\n  artist: a\nlines:\n  - text: w\n    start_ms: 1000\n    words:\n      - text: w\n        start_ms: 1000")]
    public async Task Without_usable_word_sync_the_synced_lyrics_are_used(bool hasWordSync, string lyricsfileJson)
    {
        // lyricsfileJson is already escaped for a JSON string (\n, not line breaks).
        var flag = hasWordSync ? "true" : "false";
        var (provider, _) = Create(h => h.Serve("get", _ => Json($$"""
            {"id":1,"trackName":"t","artistName":"a","albumName":"b","duration":212,"instrumental":false,
             "plainLyrics":"w","syncedLyrics":"[00:01.00]w","hasWordSync":{{flag}},"lyricsfile":"{{lyricsfileJson}}"}
            """)));

        var result = await provider.TryGetAsync(Full, CancellationToken.None);

        Assert.NotNull(result);
        var line = Assert.Single(result.Document.Lines);
        Assert.True(result.Document.IsSynced);
        Assert.Equal("w", line.Text);
        Assert.Null(line.Words);
    }

    [Fact]
    public async Task Search_prefers_a_word_synced_record_over_a_line_synced_one()
    {
        // Both within tolerance; the line-synced one is the closer length.
        var word = System.Text.Json.Nodes.JsonNode.Parse(Fixtures.Read("lrclib-get.word-synced.synthetic.json"))!;
        word["duration"] = 214.0;
        var (provider, _) = Create(h => h.Serve("search", _ => Json($$"""
            [{"id":1,"trackName":"Paper Lanterns","artistName":"The Quiet Harbour","albumName":"Tidelines",
              "duration":212,"instrumental":false,"plainLyrics":"x","syncedLyrics":"[00:01.00]Line words",
              "hasWordSync":false,"lyricsfile":null},
             {{word.ToJsonString()}}]
            """)));

        var result = await provider.TryGetAsync(Full with { Duration = TimeSpan.FromSeconds(212) }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Paper lanterns on the water", result.Document.Lines[0].Text);
        Assert.NotNull(result.Document.Lines[0].Words);
    }

    [Fact]
    public async Task Miss_on_get_falls_back_to_search_and_prefers_synced_within_tolerance()
    {
        // 212 s: the radio edit (184 s) is out; the empty record (212 s) has nothing;
        // of the plain (211 s) and synced (214.5 s) album records, synced wins.
        var (provider, handler) = Create(h => h.Serve("search", "lrclib-search.synthetic.json"));

        var result = await provider.TryGetAsync(Full with { Duration = TimeSpan.FromSeconds(212) }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Synced album words", Assert.Single(result.Document.Lines).Text);
        Assert.True(result.Document.IsSynced);
        Assert.Equal(["/api/get", "/api/search"], handler.Endpoints);
        var search = QueryOf(handler.Requests[1]);
        Assert.Equal(["track_name", "artist_name"], search.Keys);
    }

    [Fact]
    public async Task Search_rejects_results_outside_the_duration_tolerance()
    {
        // 209.5 s: only the plain 211 s record is within 3 s.
        var (provider, _) = Create(h => h.Serve("search", "lrclib-search.synthetic.json"));

        var near = await provider.TryGetAsync(Full with { Duration = TimeSpan.FromSeconds(209.5) }, CancellationToken.None);
        var far = await provider.TryGetAsync(Full with { Duration = TimeSpan.FromSeconds(300) }, CancellationToken.None);

        Assert.Equal("Unsynced album words", Assert.Single(near!.Document.Lines).Text);
        Assert.Null(far);
    }

    [Fact]
    public async Task Without_album_or_duration_it_searches_and_needs_an_exact_name_match()
    {
        var (provider, handler) = Create(h => h.Serve("search", "lrclib-search.synthetic.json"));

        var result = await provider.TryGetAsync(
            new LyricsQuery("abcDEF12_-3", "paper lanterns", "the quiet harbour"), CancellationToken.None);

        // The radio edit's title differs; of the exact-name records, synced wins.
        Assert.Equal("Synced album words", Assert.Single(result!.Document.Lines).Text);
        Assert.Equal(["/api/search"], handler.Endpoints);
    }

    [Fact]
    public async Task Search_with_no_results_is_a_miss()
    {
        var (provider, _) = Create(h => h.Serve("search", _ => Json("[]")));

        Assert.Null(await provider.TryGetAsync(Full, CancellationToken.None));
    }

    [Fact]
    public async Task Server_error_throws_so_the_chain_can_tell_it_from_a_miss()
    {
        // LRCLIB answers 503 with this body when it is busy (seen while recording).
        var (provider, _) = Create(h => h.Serve("get", "lrclib-overloaded.json", HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.TryGetAsync(Full, CancellationToken.None));
    }

    [Fact]
    public async Task A_busy_answer_is_retried_and_the_lyrics_found()
    {
        var time = new InstantDelays();
        var (provider, handler) = Create(h => h.Serve("get", InTurn(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK)), time);

        var result = await provider.TryGetAsync(AmazingGrace, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Document.IsSynced);
        Assert.Equal(["/api/get", "/api/get"], handler.Endpoints);
        Assert.Equal([LrcLibProvider.RetryDelays[0]], time.Delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Other_transient_answers_are_retried(HttpStatusCode status)
    {
        var (provider, handler) = Create(h => h.Serve("get", InTurn(status, HttpStatusCode.OK)));

        Assert.NotNull(await provider.TryGetAsync(AmazingGrace, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Repeated_busy_answers_stop_after_the_retries_and_throw()
    {
        var time = new InstantDelays();
        var (provider, handler) = Create(h => h.Serve("get", InTurn(HttpStatusCode.ServiceUnavailable)), time);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.TryGetAsync(Full, CancellationToken.None));

        Assert.Equal(["/api/get", "/api/get", "/api/get"], handler.Endpoints);
        Assert.Equal(LrcLibProvider.RetryDelays, time.Delays);
        Assert.True(time.Delays.Aggregate(TimeSpan.Zero, (a, b) => a + b) <= LrcLibProvider.RetryWithin);
    }

    [Fact]
    public async Task Repeated_busy_answers_end_unavailable_within_the_chains_budget()
    {
        var (provider, handler) = Create(h => h.Serve("get", InTurn(HttpStatusCode.ServiceUnavailable)));
        var chain = new LyricsProviderChain([provider]);

        var lookup = await chain.LookupAsync(Full, CancellationToken.None);

        Assert.Equal(LyricsStatus.Unavailable, lookup.Status);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Retry_After_is_honoured()
    {
        var time = new InstantDelays();
        var answer = InTurn(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var (provider, _) = Create(h => h.Serve("get", request =>
        {
            var busy = answer(request);
            busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return busy;
        }), time);

        Assert.NotNull(await provider.TryGetAsync(AmazingGrace, CancellationToken.None));
        Assert.Equal([TimeSpan.FromSeconds(2)], time.Delays);
    }

    [Fact]
    public async Task A_Retry_After_beyond_the_budget_fails_at_once()
    {
        var time = new InstantDelays();
        var (provider, handler) = Create(h => h.Serve("get", _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) },
        }), time);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.TryGetAsync(Full, CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task A_connection_error_is_retried()
    {
        var time = new InstantDelays();
        var calls = 0;
        var (provider, handler) = Create(h => h.Serve("get", _ => ++calls == 1
            ? throw new HttpRequestException("connection reset")
            : Json(Fixtures.Read("lrclib-get.json"))), time);

        Assert.NotNull(await provider.TryGetAsync(AmazingGrace, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(time.Delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Other_answers_are_not_retried(HttpStatusCode status)
    {
        var time = new InstantDelays();
        var (provider, handler) = Create(h => h.Serve("get", InTurn(status)), time);

        try
        {
            await provider.TryGetAsync(Full, CancellationToken.None);
        }
        catch (HttpRequestException)
        {
        }

        Assert.Equal(1, handler.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/get"));
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task Query_without_title_or_artist_asks_nothing()
    {
        var (provider, handler) = Create(_ => { });

        Assert.Null(await provider.TryGetAsync(Full with { Artist = " " }, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Query_from_track_uses_the_primary_artist()
    {
        var track = new TrackInfo
        {
            VideoId = "vid",
            Title = "Song",
            Artists = [new ArtistRef("First"), new ArtistRef("Second")],
            Album = " ",
            Duration = TimeSpan.FromSeconds(100),
        };

        var query = LyricsQuery.FromTrack(track);

        Assert.Equal(new LyricsQuery("vid", "Song", "First", null, TimeSpan.FromSeconds(100)), query);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };
}
