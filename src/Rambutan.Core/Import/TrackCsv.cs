using System.Globalization;
using System.Text;

namespace Rambutan.Core.Import;

/// <summary>
/// A CSV of songs, as Exportify, TuneMyMusic, Soundiiz and similar tools
/// write them. Columns are found by their header's letters, ignoring case,
/// spaces and punctuation ("Artist Name(s)" is <c>artistnames</c>), never by
/// their position, since every tool orders them its own way. Quoting is
/// RFC 4180's; the separator is a comma, a semicolon or a tab, whichever the
/// header line uses outside quotes.
/// </summary>
public static class TrackCsv
{
    private static readonly string[] TitleColumns =
        ["trackname", "tracktitle", "songname", "songtitle", "title", "name", "song", "track"];
    private static readonly string[] ArtistColumns = ["artistnames", "artistname", "artists", "artist"];
    private static readonly string[] AlbumColumns = ["albumname", "albumtitle", "album"];
    private static readonly string[] MillisecondColumns = ["trackdurationms", "durationms", "lengthms"];
    private static readonly string[] DurationColumns = ["duration", "length", "time"];
    private static readonly string[] IdColumns = ["trackuri", "spotifyuri", "spotifyid", "spotifytrackid", "uri"];
    private static readonly string[] IsrcColumns = ["isrc"];
    private static readonly string[] PlaylistColumns = ["playlistname", "playlist"];

    /// <summary>
    /// The lists in a CSV: one per name in its playlist column (TuneMyMusic
    /// writes several playlists to one file), in the order first seen, else
    /// one list named <paramref name="fallbackName"/> (the file's name).
    /// </summary>
    /// <exception cref="ImportFormatException">No header line, or no title column.</exception>
    public static IReadOnlyList<ImportedList> Read(string text, string fallbackName)
    {
        text = text.TrimStart('\uFEFF');
        using var rows = Rows(text, Separator(text)).GetEnumerator();
        if (!rows.MoveNext() || rows.Current.All(string.IsNullOrWhiteSpace))
            throw new ImportFormatException(ImportProblem.Unreadable, "no header line");

        var headers = rows.Current.Select(Key).ToArray();
        var title = Column(headers, TitleColumns)
                    ?? throw new ImportFormatException(ImportProblem.NoTitleColumn, "no title column");
        var artist = Column(headers, ArtistColumns);
        var album = Column(headers, AlbumColumns);
        var milliseconds = Column(headers, MillisecondColumns);
        var duration = milliseconds is null ? Column(headers, DurationColumns) : null;
        var id = Column(headers, IdColumns);
        var isrc = Column(headers, IsrcColumns);
        var playlist = Column(headers, PlaylistColumns);

        var lists = new List<(string Name, List<ImportedTrack> Tracks, int Skipped)>();
        while (rows.MoveNext())
        {
            var row = rows.Current;
            if (row.All(string.IsNullOrWhiteSpace))
                continue;
            string? Get(int? column) =>
                column is { } c && c < row.Count && row[c].Trim() is { Length: > 0 } value ? value : null;

            var name = Get(playlist) ?? fallbackName;
            var index = lists.FindIndex(l => l.Name == name);
            if (index < 0)
            {
                lists.Add((name, [], 0));
                index = lists.Count - 1;
            }

            if (Get(title) is not { } songTitle)
            {
                lists[index] = lists[index] with { Skipped = lists[index].Skipped + 1 };
                continue;
            }
            lists[index].Tracks.Add(new ImportedTrack(
                songTitle,
                Artists(Get(artist)),
                Get(album),
                milliseconds is not null ? Milliseconds(Get(milliseconds)) : Duration(Get(duration)),
                SpotifyIds.TrackId(Get(id)),
                Get(isrc)?.ToUpperInvariant()));
        }

        return lists.Select(l => new ImportedList(ImportedListKind.Playlist, l.Name, l.Tracks, l.Skipped)).ToList();
    }

    /// <summary>
    /// The artists of a cell: separated by semicolons when it has one, else by
    /// commas. Exportify writes "A, B" and escapes a comma inside a name as
    /// <c>\,</c>, which stays in the name.
    /// </summary>
    public static IReadOnlyList<string> Artists(string? cell)
    {
        if (cell is null)
            return [];
        var separator = cell.Contains(';') ? ';' : ',';
        var artists = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < cell.Length; i++)
        {
            if (cell[i] == '\\' && i + 1 < cell.Length && cell[i + 1] is ',' or ';')
            {
                current.Append(cell[++i]);
            }
            else if (cell[i] == separator)
            {
                Add();
            }
            else
            {
                current.Append(cell[i]);
            }
        }
        Add();
        return artists;

        void Add()
        {
            if (current.ToString().Trim() is { Length: > 0 } name)
                artists.Add(name);
            current.Clear();
        }
    }

    private static TimeSpan? Milliseconds(string? cell) =>
        double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms > 0 &&
        ms < TimeSpan.MaxValue.TotalMilliseconds
            ? TimeSpan.FromMilliseconds(Math.Round(ms))
            : null;

    /// <summary>
    /// A length without a unit: "3:45" or "1:02:03", else a number, read as
    /// milliseconds above 10 000 and as seconds otherwise (no song lasts
    /// 10 000 seconds; few last under 10).
    /// </summary>
    public static TimeSpan? Duration(string? cell)
    {
        if (cell is null)
            return null;
        if (cell.Contains(':'))
        {
            var parts = cell.Split(':');
            if (parts.Length > 3)
                return null;
            var seconds = 0L;
            foreach (var part in parts)
            {
                if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    return null;
                seconds = seconds * 60 + value;
            }
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        }
        if (!double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            number <= 0 || number >= TimeSpan.MaxValue.TotalMilliseconds)
        {
            return null;
        }
        return number > 10_000 ? TimeSpan.FromMilliseconds(Math.Round(number)) : TimeSpan.FromSeconds(number);
    }

    private static int? Column(string[] headers, string[] names)
    {
        foreach (var name in names)
        {
            var index = Array.IndexOf(headers, name);
            if (index >= 0)
                return index;
        }
        return null;
    }

    private static string Key(string header) =>
        new(header.Where(char.IsAsciiLetter).Select(char.ToLowerInvariant).ToArray());

    // The separator the header line uses most outside quotes; a comma when it
    // has none (a file of one column).
    private static char Separator(string text)
    {
        int commas = 0, semicolons = 0, tabs = 0;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
                quoted = !quoted;
            else if (!quoted && c is '\n' or '\r')
                break;
            else if (!quoted && c == ',')
                commas++;
            else if (!quoted && c == ';')
                semicolons++;
            else if (!quoted && c == '\t')
                tabs++;
        }
        return tabs > commas && tabs > semicolons ? '\t' : semicolons > commas ? ';' : ',';
    }

    /// <summary>RFC 4180 records: a quoted field may hold the separator,
    /// line breaks and doubled quotes; CRLF and LF both end a record.</summary>
    private static IEnumerable<IReadOnlyList<string>> Rows(string text, char separator)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            any = true;
            if (quoted)
            {
                if (c != '"')
                    field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"')
                    field.Append(text[++i]);
                else
                    quoted = false;
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                fields.Add(field.ToString());
                field.Clear();
                yield return fields;
                fields = [];
                any = false;
            }
            else
            {
                field.Append(c);
            }
        }
        if (any)
        {
            fields.Add(field.ToString());
            yield return fields;
        }
    }
}
