using System.Text.Json;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// YouTube Music's lyrics: the watch page's Lyrics tab (the next-* recordings),
/// then the browse it opens. browse-lyrics.k8qwmwvqxgi.json is a recording whose
/// lyric text is replaced with invented lines (the fixtures README says why);
/// browse-lyrics.not-available.json is recorded as it came. The
/// browse-lyrics-mobile-* recordings are the same browse asked as the Android
/// app, lyric text invented too: timed, untimed, and none.
/// </summary>
public class LyricsTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static JsonDocument LoadFixture(string name) => JsonDocument.Parse(Fixture(name));

    [Theory]
    [InlineData("next-radio.khnokw3mw24.json", "MPLYt_K8qWMWVqXGi-5")]
    [InlineData("next-mix.rdaoni3jl65kf37f9jysjm8dgg.json", "MPLYt_7ltM34kr0mH-4")]
    // Marked unselectable on an official music video; its browse answers anyway.
    [InlineData("next-radio.yqhsxmglc9a.json", "MPLYt_2SSDRLbCIui")]
    public void The_lyrics_tab_is_found_by_its_page_type(string fixture, string browseId)
    {
        using var doc = LoadFixture(fixture);

        Assert.Equal(browseId, LyricsParser.ParseBrowseId(doc.RootElement));
    }

    [Fact]
    public void A_watch_page_without_tabs_has_no_lyrics_tab()
    {
        using var doc = LoadFixture("next-rated.synthetic.json");

        Assert.Null(LyricsParser.ParseBrowseId(doc.RootElement));
    }

    [Fact]
    public void The_lyrics_are_the_shelfs_text_with_its_footer_as_the_credit()
    {
        using var doc = LoadFixture("browse-lyrics.k8qwmwvqxgi.json");

        var lyrics = Assert.IsType<TrackLyrics>(LyricsParser.Parse(doc.RootElement));

        Assert.StartsWith("Paper lanterns over the harbour wall\nCounting boats", lyrics.Text);
        // Stanzas stay apart, as YouTube Music shows them.
        Assert.Contains("like a sign\n\nHold on", lyrics.Text);
        Assert.Equal("Source: Musixmatch", lyrics.Credit);
    }

    [Fact]
    public void A_track_without_lyrics_has_none()
    {
        using var doc = LoadFixture("browse-lyrics.not-available.json");

        Assert.Null(LyricsParser.Parse(doc.RootElement));
    }

    [Fact]
    public async Task A_lookup_opens_the_watch_page_without_a_queue_then_its_lyrics_tab()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);
        handler.Response = sent => sent.Endpoint == "next"
            ? Fixture("next-radio.khnokw3mw24.json")
            : Fixture("browse-lyrics-mobile.khnokw3mw24.json");

        var lyrics = await client.GetLyricsAsync("khnokW3Mw24");

        Assert.NotNull(lyrics);
        Assert.Equal(["next", "browse"], handler.Requests.Select(r => r.Endpoint));
        var next = handler.Requests[0].Body;
        Assert.Equal("khnokW3Mw24", next.GetProperty("videoId").GetString());
        Assert.False(next.TryGetProperty("playlistId", out _));
        Assert.Equal("WEB_REMIX", ClientName(handler.Requests[0]));
        Assert.Equal("MPLYt_K8qWMWVqXGi-5", handler.Requests[1].Body.GetProperty("browseId").GetString());
        Assert.Equal("ANDROID_MUSIC", ClientName(handler.Requests[1]));
        Assert.NotNull(lyrics.Lines);
    }

    [Fact]
    public void Timed_lyrics_carry_each_lines_cue_and_the_credit()
    {
        using var doc = LoadFixture("browse-lyrics-mobile.khnokw3mw24.json");

        var lyrics = LyricsParser.ParseTimed(doc.RootElement);

        Assert.NotNull(lyrics);
        var lines = lyrics.Lines!;
        Assert.Equal(82, lines.Count);
        Assert.Equal(new TimedLyricLine(TimeSpan.Zero, TimeSpan.FromMilliseconds(26520), "♪"), lines[0]);
        Assert.Equal(new TimedLyricLine(TimeSpan.FromMilliseconds(26520), TimeSpan.FromMilliseconds(30920),
            "Paper lanterns over the harbour wall"), lines[1]);
        Assert.Equal(TimeSpan.FromMilliseconds(297130), lines[^1].End);
        Assert.Equal("Source: Musixmatch", lyrics.Credit);
        Assert.Equal(string.Join('\n', lines.Select(l => l.Text)), lyrics.Text);
    }

    [Theory]
    // Lines without cues: the web browse gives them, with the stanzas apart.
    [InlineData("browse-lyrics-mobile.yqhsxmglc9a.json")]
    [InlineData("browse-lyrics-mobile.not-available.json")]
    [InlineData("browse-lyrics.k8qwmwvqxgi.json")]
    public void A_browse_without_timed_lines_has_no_timed_lyrics(string fixture)
    {
        using var doc = LoadFixture(fixture);

        Assert.Null(LyricsParser.ParseTimed(doc.RootElement));
    }

    [Theory]
    [InlineData("browse-lyrics-mobile.yqhsxmglc9a.json")]
    [InlineData("error")]
    public async Task Without_timed_lines_the_web_browse_gives_plain_lyrics(string mobileAnswer)
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);
        handler.Response = sent => sent.Endpoint == "next" ? Fixture("next-radio.khnokw3mw24.json")
            : ClientName(sent) == "WEB_REMIX" ? Fixture("browse-lyrics.k8qwmwvqxgi.json")
            : mobileAnswer == "error" ? throw new HttpRequestException("refused")
            : Fixture(mobileAnswer);

        var lyrics = await client.GetLyricsAsync("khnokW3Mw24");

        Assert.NotNull(lyrics);
        Assert.Null(lyrics.Lines);
        Assert.Equal(["WEB_REMIX", "ANDROID_MUSIC", "WEB_REMIX"], handler.Requests.Select(ClientName));
        Assert.Equal(["next", "browse", "browse"], handler.Requests.Select(r => r.Endpoint));
    }

    private static string? ClientName(RecordingHandler.Sent sent) =>
        sent.Body.GetProperty("context").GetProperty("client").GetProperty("clientName").GetString();

    [Fact]
    public async Task No_lyrics_tab_means_no_lyrics_and_no_browse()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);
        handler.Response = _ => Fixture("next-rated.synthetic.json");

        Assert.Null(await client.GetLyricsAsync("khnokW3Mw24"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task An_id_that_isnt_YouTubes_has_no_lyrics_and_asks_nothing()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);

        Assert.Null(await client.GetLyricsAsync("local:1"));
        Assert.Empty(handler.Requests);
    }
}
