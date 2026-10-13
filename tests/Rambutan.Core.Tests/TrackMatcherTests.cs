using System.Text.Json;
using Rambutan.Core.Models;
using Rambutan.Core.Stations;

namespace Rambutan.Core.Tests;

public class TrackMatcherTests
{
    private static TrackInfo Song(string title, string artist, int? seconds = null, string? id = null) => new()
    {
        VideoId = id ?? $"{artist}:{title}",
        Title = title,
        Artists = [new ArtistRef(artist)],
        Duration = seconds is { } s ? TimeSpan.FromSeconds(s) : null,
    };

    private static StationTrack Track(string artist, string title, int? seconds = null, string? videoId = null) =>
        new(artist, title, seconds is { } s ? TimeSpan.FromSeconds(s) : null, videoId);

    /// <summary>
    /// Real station tracks with YouTube Music's top five song results for each:
    /// <c>station-matches.json</c> holds 269 from three Last.fm users' mix,
    /// recommendations and library (2026-10-10, no usernames), and
    /// <c>artist-station-matches.json</c> 48 from the Daft Punk and Florence +
    /// the Machine artist stations (2026-10-13). The expected outcomes are
    /// a snapshot of the current rules. The cases the evaluation flagged (a later
    /// result, a duration far off, or Last.fm's own link) were checked by hand;
    /// the plain top-result matches weren't. A rule change that moves any of them
    /// shows up here, to be judged.
    /// </summary>
    [Theory]
    [InlineData("station-matches.json", 260)]
    [InlineData("artist-station-matches.json", 48)]
    public void Recorded_stations_resolve_as_reviewed(string fixture, int expectedSongs)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Stations", fixture);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        static TimeSpan? Seconds(JsonElement e) => e.ValueKind == JsonValueKind.Number ? TimeSpan.FromSeconds(e.GetInt32()) : null;

        var wrong = new List<string>();
        var songs = 0;
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            var track = new StationTrack(
                c.GetProperty("artist").GetString()!, c.GetProperty("title").GetString()!,
                Seconds(c.GetProperty("seconds")), c.GetProperty("videoId").GetString());
            var results = c.GetProperty("results").EnumerateArray().Select(r => new TrackInfo
            {
                VideoId = r.GetProperty("videoId").GetString()!,
                Title = r.GetProperty("title").GetString()!,
                Artists = r.GetProperty("artists").EnumerateArray().Select(a => new ArtistRef(a.GetString()!)).ToList(),
                Duration = Seconds(r.GetProperty("seconds")),
            }).ToList();

            var row = StationResolver.Resolve(track, results);
            var got = row.Match == StationMatch.Song ? row.Track!.VideoId : row.Match.ToString();
            var expected = c.GetProperty("expect").GetString();
            if (got != expected)
                wrong.Add($"{track.Artist} – {track.Title}: expected {expected}, got {got}");
            if (row.Match == StationMatch.Song)
                songs++;
        }

        Assert.Empty(wrong);
        Assert.Equal(expectedSongs, songs);
    }

    [Fact]
    public void Same_title_by_another_artist_is_not_a_match()
    {
        // Last.fm gave no duration, so nothing can vouch for an alias.
        Assert.Null(TrackMatcher.Pick(Track("Ondara", "Give Me A Moment"),
            [Song("Give Me A Moment", "The Jet Stone Conspiracy", 215)]));
        Assert.Null(TrackMatcher.Pick(Track("SR-71", "Here We Go Again", 185),
            [Song("Here We Go Again", "Miracle Of Sound", 215)]));
    }

    [Fact]
    public void Same_title_and_length_under_another_name_is_an_alias()
    {
        var css = Song("You Could Have It All", "CSS", 226);
        Assert.Same(css, TrackMatcher.Pick(Track("Cansei de Ser Sexy", "You Could Have It All", 225), [css]));
        Assert.Null(TrackMatcher.Pick(Track("Cansei de Ser Sexy", "You Could Have It All", 229), [css]));
    }

    [Fact]
    public void A_wrong_duration_does_not_reject_a_name_match()
    {
        // Last.fm says 46 s; the song is 5:19.
        var song = Song("Love Island", "Fatboy Slim", 319);
        Assert.Same(song, TrackMatcher.Pick(Track("Fatboy Slim", "Love Island", 46), [song]));
    }

    [Fact]
    public void Duration_ranks_candidates_of_the_same_kind()
    {
        var single = Song("Arrows of Eros (Single Version)", "Golden Silvers", 210);
        var album = Song("Arrows Of Eros", "Golden Silvers", 251);
        Assert.Same(album, TrackMatcher.Pick(Track("Golden Silvers", "Arrows Of Eros", 251), [single, album]));
    }

    [Fact]
    public void A_name_match_of_the_right_length_beats_an_alias()
    {
        var alias = Song("Julius", "STRFKR", 228);
        var same = Song("Julius", "Starfucker", 240);
        Assert.Same(same, TrackMatcher.Pick(Track("Starfucker", "Julius", 228), [alias, same]));
    }

    [Theory]
    [InlineData("Colours", "Colours (Fred Falke Remix)")]
    [InlineData("Colours (Fred Falke Remix)", "Colours (Remixed By Fred Falke)", true)]
    [InlineData("That Kiss", "That Kiss (Live at Castlefield Bowl)")]
    [InlineData("That Kiss", "That Kiss (Acoustic / Remastered 2022)")]
    [InlineData("Dancing", "Dancing (Extended mix)")]
    [InlineData("Soared Straight Through Me", "Soared Straight Through Me - Kareem Ali Remix")]
    public void Another_version_is_a_last_resort(string stationTitle, string songTitle, bool same = false)
    {
        var song = Song(songTitle, "Artist", 200);
        var withLink = Track("Artist", stationTitle, 200, videoId: "lastfm");
        Assert.Equal(same ? song : null, TrackMatcher.Pick(withLink, [song]));
        Assert.Same(song, TrackMatcher.Pick(withLink, [song], lastResort: true));
    }

    [Theory]
    [InlineData("Stairway to Heaven - Remaster", "Stairway to Heaven (Remaster)")]
    [InlineData("Heaven Knows I'm Miserable Now - 2011 Remaster", "Heaven Knows I'm Miserable Now")]
    [InlineData("High School Never Ends - Main Version - Explicit", "High School Never Ends (Main Version - Explicit)")]
    [InlineData("Where I Wanna Be (Feat. Kurupt & Nate Dogg)", "Where I Wanna Be")]
    [InlineData("Ain't No Love", "Ain’t No Love")]
    [InlineData("Impressões Digitais", "Impressões digitais")]
    [InlineData("Latin' América", "Latin'America")]
    [InlineData("The Ecstacy of Gold", "The Ecstasy Of Gold (2004 Remaster)")]
    public void Qualifiers_spelling_and_case_do_not_matter(string stationTitle, string songTitle)
    {
        var song = Song(songTitle, "Artist");
        Assert.Same(song, TrackMatcher.Pick(Track("Artist", stationTitle), [song]));
    }

    [Theory]
    [InlineData("Home", "Welcome Home")]
    [InlineData("Loving You", "mangetout")]
    [InlineData("Shee", "She")]
    public void Different_titles_do_not_match(string stationTitle, string songTitle) =>
        Assert.Null(TrackMatcher.Pick(Track("Artist", stationTitle), [Song(songTitle, "Artist")], lastResort: true));

    [Theory]
    [InlineData("Joan Jett and the Blackhearts", "Joan Jett")]
    [InlineData("The Courteeners", "Courteeners")]
    [InlineData("Booker T. & the MG's", "Booker T and the MG's")]
    [InlineData("Jafu'mega", "Jafumega")]
    [InlineData("lx-90", "LX-90")]
    public void Artist_names_match_word_for_word(string stationArtist, string songArtist)
    {
        var song = Song("Title", songArtist);
        Assert.Same(song, TrackMatcher.Pick(Track(stationArtist, "Title"), [song]));
    }

    [Fact]
    public void An_artist_is_not_matched_by_a_word_inside_another_name() =>
        Assert.Null(TrackMatcher.Pick(Track("Miranda", "Tear Us Apart"), [Song("Tear Us Apart", "Stanton-Miranda")]));
}
