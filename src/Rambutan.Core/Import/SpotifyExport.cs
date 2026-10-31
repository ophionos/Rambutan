using System.Globalization;
using System.Text.Json;

namespace Rambutan.Core.Import;

/// <summary>
/// Spotify's data export (the account privacy page's "Account data"): a zip
/// whose <c>Playlist1.json</c>, <c>Playlist2.json</c>, … list every playlist,
/// private ones too, and whose <c>YourLibrary.json</c> lists Liked Songs. The
/// zip's folder has had other names over the years, so only a file's own name
/// counts, and a file is read by its keys, never trusted by its name. Opening
/// the zip is the caller's: this reads text only.
/// </summary>
public static class SpotifyExport
{
    private const string LocalPrefix = "spotify:local:";

    /// <summary>Whether a file of the export, by its path in the zip, is one
    /// this reads.</summary>
    public static bool IsListFile(string path)
    {
        var name = FileName(path);
        return name.Equals("YourLibrary.json", StringComparison.OrdinalIgnoreCase) ||
               (name.StartsWith("Playlist", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The lists in the export's files, Liked Songs first, then the playlists
    /// in the export's order (Playlist1.json before Playlist2.json before
    /// Playlist10.json, each file's in its own order).
    /// </summary>
    /// <exception cref="ImportFormatException">A file isn't one of the export's.</exception>
    public static IReadOnlyList<ImportedList> ReadAll(IEnumerable<(string Path, string Json)> files)
    {
        var lists = new List<ImportedList>();
        foreach (var (_, json) in files.OrderBy(f => Number(f.Path)).ThenBy(f => f.Path, StringComparer.Ordinal))
            lists.AddRange(Read(json));
        return
        [
            .. lists.Where(l => l.Kind == ImportedListKind.LikedSongs),
            .. lists.Where(l => l.Kind != ImportedListKind.LikedSongs),
        ];
    }

    /// <summary>The lists in one of the export's files: a file of playlists,
    /// or the library's (Liked Songs, when it has any).</summary>
    /// <exception cref="ImportFormatException">Not JSON, or not one of the export's files.</exception>
    public static IReadOnlyList<ImportedList> Read(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new ImportFormatException(ImportProblem.Unreadable, "not JSON", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ImportFormatException(ImportProblem.Unreadable, "not a file of Spotify's data export");
            if (root.TryGetProperty("playlists", out var playlists))
                return Playlists(playlists);
            // YourLibrary.json also lists albums, artists, shows and episodes;
            // only the songs are brought over.
            if (root.TryGetProperty("tracks", out var tracks))
            {
                return tracks.ValueKind == JsonValueKind.Array && tracks.GetArrayLength() > 0
                    ? [LikedSongs(tracks)]
                    : [];
            }
            if (root.TryGetProperty("artists", out _) || root.TryGetProperty("albums", out _))
                return [];
            throw new ImportFormatException(ImportProblem.Unreadable, "not a file of Spotify's data export");
        }
    }

    private static List<ImportedList> Playlists(JsonElement playlists)
    {
        var lists = new List<ImportedList>();
        if (playlists.ValueKind != JsonValueKind.Array)
            return lists;
        foreach (var playlist in playlists.EnumerateArray())
        {
            if (playlist.ValueKind != JsonValueKind.Object)
                continue;
            var tracks = new List<ImportedTrack>();
            var skipped = 0;
            if (playlist.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (Song(item) is { } track)
                        tracks.Add(track);
                    else
                        skipped++;
                }
            }
            lists.Add(new ImportedList(ImportedListKind.Playlist, String(playlist, "name") ?? "", tracks, skipped));
        }
        return lists;
    }

    // An item holds one of "track", "localTrack", "episode" or "audiobook";
    // the others are null. Only the first two are songs.
    private static ImportedTrack? Song(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        if (item.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object)
        {
            return String(track, "trackName") is { } title
                ? new ImportedTrack(title, Artists(String(track, "artistName")), String(track, "albumName"),
                    SpotifyId: SpotifyIds.TrackId(String(track, "trackUri")))
                : null;
        }
        if (item.TryGetProperty("localTrack", out var local) && local.ValueKind == JsonValueKind.Object)
            return LocalTrack(String(local, "uri"));
        return null;
    }

    private static ImportedList LikedSongs(JsonElement tracks)
    {
        var songs = new List<ImportedTrack>();
        var skipped = 0;
        foreach (var track in tracks.EnumerateArray())
        {
            if (track.ValueKind == JsonValueKind.Object && String(track, "track") is { } title)
            {
                songs.Add(new ImportedTrack(title, Artists(String(track, "artist")), String(track, "album"),
                    SpotifyId: SpotifyIds.TrackId(String(track, "uri"))));
            }
            else
            {
                skipped++;
            }
        }
        return new ImportedList(ImportedListKind.LikedSongs, "", songs, skipped);
    }

    /// <summary>
    /// A file the user added to Spotify from their own disk:
    /// <c>spotify:local:&lt;artist&gt;:&lt;album&gt;:&lt;title&gt;:&lt;seconds&gt;</c>,
    /// form-encoded. Still a song, and often one YouTube Music has. Null when
    /// the URI isn't one or names no title.
    /// </summary>
    public static ImportedTrack? LocalTrack(string? uri)
    {
        if (uri is null || !uri.StartsWith(LocalPrefix, StringComparison.Ordinal))
            return null;
        var fields = uri[LocalPrefix.Length..].Split(':');
        if (fields.Length != 4)
            return null;
        var (artist, album, title) = (Decode(fields[0]), Decode(fields[1]), Decode(fields[2]));
        if (title is null)
            return null;
        TimeSpan? duration = int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
                             seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
        return new ImportedTrack(title, artist is null ? [] : [artist], album, duration);
    }

    private static string? Decode(string field)
    {
        try
        {
            var text = Uri.UnescapeDataString(field.Replace('+', ' ')).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    // The export names one artist per song: the first credited.
    private static IReadOnlyList<string> Artists(string? artist) => artist is null ? [] : [artist];

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString()!.Trim() is { Length: > 0 } text
            ? text
            : null;

    private static string FileName(string path) =>
        path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];

    // Playlist10.json sorts after Playlist2.json; YourLibrary.json, with no
    // number, first.
    private static long Number(string path)
    {
        var digits = new string(FileName(path).Where(char.IsAsciiDigit).ToArray());
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;
    }
}
