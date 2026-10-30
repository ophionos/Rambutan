using Rambutan.Core.Import;

namespace Rambutan.Core.Tests;

/// <summary>
/// Spotify's data export and the CSVs other tools write. The files here are
/// hand-made in the shapes those tools write (Exportify's header is its
/// README's; the export's keys are its files'), with made-up ids.
/// </summary>
public sealed class SpotifyExportTests
{
    private const string Playlists = """
        {
          "playlists": [
            {
              "name": "Road Trip",
              "lastModifiedDate": "2024-05-01",
              "items": [
                { "track": { "trackName": "Panama", "artistName": "Van Halen", "albumName": "1984",
                             "trackUri": "spotify:track:05RgAMGypEvqhNs5hPCbMS" },
                  "episode": null, "localTrack": null, "audiobook": null, "addedDate": "2024-01-01" },
                { "track": null,
                  "episode": { "episodeName": "Pod", "showName": "Show", "episodeUri": "spotify:episode:x" },
                  "localTrack": null, "audiobook": null },
                { "track": null, "episode": null,
                  "localTrack": { "uri": "spotify:local:Daft+Punk:Discovery:One+More+Time%3A+Edit:320" },
                  "audiobook": null },
                { "track": { "trackName": "", "artistName": "Nobody", "albumName": "", "trackUri": "" } },
                { "track": { "trackName": "Panama", "artistName": "Van Halen", "albumName": "1984",
                             "trackUri": "spotify:track:05RgAMGypEvqhNs5hPCbMS" } }
              ],
              "description": null,
              "numberOfFollowers": 0
            },
            { "name": "Empty", "items": [] }
          ]
        }
        """;

    [Fact]
    public void A_playlist_keeps_its_songs_in_order_with_duplicates_and_counts_the_rest()
    {
        var lists = SpotifyExport.Read(Playlists);

        Assert.Equal(["Road Trip", "Empty"], lists.Select(l => l.Name));
        var trip = lists[0];
        Assert.Equal(ImportedListKind.Playlist, trip.Kind);
        Assert.Equal(["Panama", "One More Time: Edit", "Panama"], trip.Tracks.Select(t => t.Title));
        Assert.Equal(2, trip.Skipped);
        var panama = trip.Tracks[0];
        Assert.Equal(("Van Halen", "1984", "05RgAMGypEvqhNs5hPCbMS"),
            (Assert.Single(panama.Artists), panama.Album, panama.SpotifyId));
        Assert.Null(panama.Duration);
        Assert.Empty(lists[1].Tracks);
    }

    [Fact]
    public void A_file_from_disk_is_a_song_read_from_its_uri()
    {
        var local = SpotifyExport.Read(Playlists)[0].Tracks[1];

        Assert.Equal("One More Time: Edit", local.Title);
        Assert.Equal(["Daft Punk"], local.Artists);
        Assert.Equal("Discovery", local.Album);
        Assert.Equal(TimeSpan.FromSeconds(320), local.Duration);
        Assert.Null(local.SpotifyId);
    }

    [Theory]
    [InlineData("spotify:local:::Title:0", "Title")]
    [InlineData("spotify:local:A:B:C", null)]
    [InlineData("spotify:local:A:B::200", null)]
    [InlineData("spotify:track:05RgAMGypEvqhNs5hPCbMS", null)]
    public void A_local_uri_needs_four_fields_and_a_title(string uri, string? title)
    {
        Assert.Equal(title, SpotifyExport.LocalTrack(uri)?.Title);
    }

    [Fact]
    public void The_library_file_gives_Liked_Songs_and_nothing_else()
    {
        const string library = """
            {
              "tracks": [
                { "artist": "Madonna", "album": "True Blue", "track": "La Isla Bonita",
                  "uri": "spotify:track:6r8k1vznHrzlEKYxL4dZEe" },
                { "artist": "Nobody", "album": "", "track": "  ", "uri": "" }
              ],
              "albums": [ { "artist": "Van Halen", "album": "1984", "uri": "spotify:album:x" } ],
              "artists": [ { "name": "Van Halen", "uri": "spotify:artist:y" } ],
              "shows": [], "episodes": []
            }
            """;

        var liked = Assert.Single(SpotifyExport.Read(library));

        Assert.Equal(ImportedListKind.LikedSongs, liked.Kind);
        Assert.Equal("", liked.Name);
        var song = Assert.Single(liked.Tracks);
        Assert.Equal(("La Isla Bonita", "Madonna", "True Blue", "6r8k1vznHrzlEKYxL4dZEe"),
            (song.Title, song.Artists[0], song.Album, song.SpotifyId));
        Assert.Equal(1, liked.Skipped);
    }

    [Fact]
    public void A_library_without_songs_has_no_lists()
    {
        Assert.Empty(SpotifyExport.Read("""{ "tracks": [], "artists": [] }"""));
        Assert.Empty(SpotifyExport.Read("""{ "artists": [ { "name": "A" } ] }"""));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "streams": [] }""")]
    [InlineData("[]")]
    public void Anything_else_is_unreadable(string json)
    {
        var ex = Assert.Throws<ImportFormatException>(() => SpotifyExport.Read(json));

        Assert.Equal(ImportProblem.Unreadable, ex.Problem);
    }

    [Fact]
    public void The_files_are_read_Liked_Songs_first_then_playlists_by_number()
    {
        static string One(string name) =>
            $$"""{ "playlists": [ { "name": "{{name}}", "items": [] } ] }""";
        const string library = """{ "tracks": [ { "artist": "A", "track": "Song", "uri": "" } ] }""";

        var lists = SpotifyExport.ReadAll([
            ("Spotify Account Data/Playlist10.json", One("Ten")),
            ("Spotify Account Data/Playlist2.json", One("Two")),
            ("Spotify Account Data/YourLibrary.json", library),
            ("Spotify Account Data/Playlist1.json", One("One")),
        ]);

        Assert.Equal(["", "One", "Two", "Ten"], lists.Select(l => l.Name));
        Assert.Equal(ImportedListKind.LikedSongs, lists[0].Kind);
    }

    [Theory]
    [InlineData("Spotify Account Data/Playlist1.json", true)]
    [InlineData("MyData/YourLibrary.json", true)]
    [InlineData("MyData\\playlist12.json", true)]
    [InlineData("Spotify Account Data/StreamingHistory_music_0.json", false)]
    [InlineData("Spotify Account Data/Playlist1.pdf", false)]
    public void Only_the_list_files_of_the_export_are_read(string path, bool read)
    {
        Assert.Equal(read, SpotifyExport.IsListFile(path));
    }
}

public sealed class SpotifyIdsTests
{
    [Theory]
    [InlineData("spotify:track:05RgAMGypEvqhNs5hPCbMS", "05RgAMGypEvqhNs5hPCbMS")]
    [InlineData("https://open.spotify.com/track/05RgAMGypEvqhNs5hPCbMS?si=abc", "05RgAMGypEvqhNs5hPCbMS")]
    [InlineData("https://open.spotify.com/intl-de/track/05RgAMGypEvqhNs5hPCbMS", "05RgAMGypEvqhNs5hPCbMS")]
    [InlineData(" 05RgAMGypEvqhNs5hPCbMS ", "05RgAMGypEvqhNs5hPCbMS")]
    [InlineData("https://example.com/track/05RgAMGypEvqhNs5hPCbMS", null)]
    [InlineData("spotify:album:05RgAMGypEvqhNs5hPCbMS", null)]
    [InlineData("spotify:local:A:B:C:1", null)]
    [InlineData("05RgAMGypEvqhNs5hPCbM", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_track_id_is_read_from_a_uri_a_link_or_on_its_own(string? value, string? id)
    {
        Assert.Equal(id, SpotifyIds.TrackId(value));
    }
}

public sealed class TrackCsvTests
{
    // Exportify's columns, as its README lists them; every value quoted, as it writes.
    private const string Exportify =
        "\"Track URI\",\"Track Name\",\"Artist URI(s)\",\"Artist Name(s)\",\"Album URI\",\"Album Name\"," +
        "\"Album Artist URI(s)\",\"Album Artist Name(s)\",\"Album Release Date\",\"Album Image URL\"," +
        "\"Disc Number\",\"Track Number\",\"Track Duration (ms)\",\"Track Preview URL\",\"Explicit\"," +
        "\"Popularity\",\"ISRC\",\"Added By\",\"Added At\"\r\n" +
        "\"spotify:track:05RgAMGypEvqhNs5hPCbMS\",\"Panama\",\"spotify:artist:a\",\"Van Halen\",\"spotify:album:b\"," +
        "\"1984\",\"spotify:artist:a\",\"Van Halen\",\"1984-01-04\",\"\",\"1\",\"3\",\"211373\",\"\",\"false\"," +
        "\"70\",\"usWB10400044\",\"\",\"2024-01-01T00:00:00Z\"\r\n" +
        "\"spotify:track:2Foc5Q5nqNiosCNqttzHof\",\"Get Lucky (feat. Pharrell Williams, Nile Rodgers)\"," +
        "\"spotify:artist:c, spotify:artist:d\",\"Daft Punk, Pharrell Williams, Earth\\, Wind & Fire\"," +
        "\"spotify:album:e\",\"Random Access Memories\",\"\",\"Daft Punk\",\"2013\",\"\",\"1\",\"8\",\"369626\"," +
        "\"\",\"false\",\"80\",\"\",\"\",\"\"\r\n";

    [Fact]
    public void Exportify_is_read_by_its_column_names()
    {
        var list = Assert.Single(TrackCsv.Read(Exportify, "Road Trip"));

        Assert.Equal("Road Trip", list.Name);
        Assert.Equal(ImportedListKind.Playlist, list.Kind);
        var panama = list.Tracks[0];
        Assert.Equal("Panama", panama.Title);
        Assert.Equal(["Van Halen"], panama.Artists);
        Assert.Equal("1984", panama.Album);
        Assert.Equal(TimeSpan.FromMilliseconds(211373), panama.Duration);
        Assert.Equal("05RgAMGypEvqhNs5hPCbMS", panama.SpotifyId);
        Assert.Equal("USWB10400044", panama.Isrc);

        var lucky = list.Tracks[1];
        Assert.Equal("Get Lucky (feat. Pharrell Williams, Nile Rodgers)", lucky.Title);
        Assert.Equal(["Daft Punk", "Pharrell Williams", "Earth, Wind & Fire"], lucky.Artists);
        Assert.Null(lucky.Isrc);
    }

    [Fact]
    public void One_TuneMyMusic_file_holds_several_playlists_in_the_order_first_seen()
    {
        const string tuneMyMusic =
            "Track name,Artist name,Album,Playlist name,Type,ISRC,Spotify - id\n" +
            "Panama,Van Halen,1984,Rock,Playlist,USWB10400044,05RgAMGypEvqhNs5hPCbMS\n" +
            "One More Time,Daft Punk,Discovery,Dance,Playlist,,\n" +
            "Jump,Van Halen,1984,Rock,Playlist,,\n" +
            ",Nobody,,Rock,Playlist,,\n";

        var lists = TrackCsv.Read(tuneMyMusic, "file");

        Assert.Equal(["Rock", "Dance"], lists.Select(l => l.Name));
        Assert.Equal(["Panama", "Jump"], lists[0].Tracks.Select(t => t.Title));
        Assert.Equal(1, lists[0].Skipped);
        Assert.Equal("05RgAMGypEvqhNs5hPCbMS", lists[0].Tracks[0].SpotifyId);
        Assert.Null(lists[0].Tracks[1].SpotifyId);
        Assert.Equal(["Daft Punk"], lists[1].Tracks[0].Artists);
    }

    [Fact]
    public void A_semicolon_file_with_a_byte_order_mark_and_quoted_line_breaks_is_read()
    {
        const string soundiiz =
            "﻿title;artist;album;isrc\r\n" +
            "\"Line\r\nBreak\";\"A; B\";\"Album \"\"Quoted\"\"\";\r\n" +
            "\r\n" +
            "Second;C;;";

        var list = Assert.Single(TrackCsv.Read(soundiiz, "Soundiiz"));

        Assert.Equal(["Line\r\nBreak", "Second"], list.Tracks.Select(t => t.Title));
        Assert.Equal(["A", "B"], list.Tracks[0].Artists);
        Assert.Equal("Album \"Quoted\"", list.Tracks[0].Album);
        Assert.Null(list.Tracks[1].Album);
    }

    [Fact]
    public void A_tab_separated_file_is_read()
    {
        var list = Assert.Single(TrackCsv.Read("Song\tArtist\tLength\nPanama\tVan Halen\t3:31\n", "Tabs"));

        Assert.Equal(("Panama", "Van Halen"), (list.Tracks[0].Title, list.Tracks[0].Artists[0]));
        Assert.Equal(TimeSpan.FromSeconds(211), list.Tracks[0].Duration);
    }

    [Fact]
    public void A_file_without_a_title_column_says_so()
    {
        var ex = Assert.Throws<ImportFormatException>(() => TrackCsv.Read("Artist,Album\nVan Halen,1984\n", "x"));

        Assert.Equal(ImportProblem.NoTitleColumn, ex.Problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("﻿")]
    [InlineData(",,\n")]
    public void A_file_without_a_header_is_unreadable(string text)
    {
        var ex = Assert.Throws<ImportFormatException>(() => TrackCsv.Read(text, "x"));

        Assert.Equal(ImportProblem.Unreadable, ex.Problem);
    }

    [Fact]
    public void A_header_only_file_has_no_lists()
    {
        Assert.Empty(TrackCsv.Read("Title,Artist\n", "Empty"));
    }

    [Theory]
    [InlineData("3:45", 225)]
    [InlineData("1:02:03", 3723)]
    [InlineData("225", 225)]
    [InlineData("225000", 225)]
    [InlineData("0", null)]
    [InlineData("3:x", null)]
    [InlineData("1:2:3:4", null)]
    [InlineData("soon", null)]
    public void A_length_without_a_unit_is_read_by_its_size(string cell, int? seconds)
    {
        Assert.Equal(seconds is { } s ? TimeSpan.FromSeconds(s) : null, TrackCsv.Duration(cell));
    }

    [Theory]
    [InlineData("A, B", new[] { "A", "B" })]
    [InlineData("A; B, C", new[] { "A", "B, C" })]
    [InlineData("Earth\\, Wind & Fire", new[] { "Earth, Wind & Fire" })]
    [InlineData(" , ", new string[0])]
    public void Artists_are_split_on_semicolons_else_commas(string cell, string[] artists)
    {
        Assert.Equal(artists, TrackCsv.Artists(cell));
    }
}
