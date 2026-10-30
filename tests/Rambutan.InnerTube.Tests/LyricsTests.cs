using System.Text.Json;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// YouTube Music's lyrics: the watch page's Lyrics tab (the next-* recordings),
/// then the browse it opens. browse-lyrics.k8qwmwvqxgi.json is a recording whose
/// lyric text is replaced with invented lines (the fixtures README says why);
/// browse-lyrics.not-available.json is recorded as it came.
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
            : Fixture("browse-lyrics.k8qwmwvqxgi.json");

        var lyrics = await client.GetLyricsAsync("khnokW3Mw24");

        Assert.NotNull(lyrics);
        Assert.Equal(["next", "browse"], handler.Requests.Select(r => r.Endpoint));
        var next = handler.Requests[0].Body;
        Assert.Equal("khnokW3Mw24", next.GetProperty("videoId").GetString());
        Assert.False(next.TryGetProperty("playlistId", out _));
        Assert.Equal("MPLYt_K8qWMWVqXGi-5", handler.Requests[1].Body.GetProperty("browseId").GetString());
    }

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
