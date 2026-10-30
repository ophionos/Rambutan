using System.Text.Json;

namespace Rambutan.InnerTube.Parsing;

/// <summary>A track's lyrics as YouTube Music shows them.</summary>
/// <param name="Text">The lyrics as plain text, a line per line.</param>
/// <param name="Credit">YouTube Music's own line naming where the text came from
/// ("Source: Musixmatch"), as it wrote it; null when it gave none.</param>
/// <param name="Lines">The lines with their timings, when YouTube Music gave
/// them (its Android app's answer); null for plain lyrics. Line-level only:
/// YouTube Music times no words.</param>
public sealed record TrackLyrics(string Text, string? Credit, IReadOnlyList<TimedLyricLine>? Lines = null);

/// <summary>A lyric line YouTube Music times: shown from <see cref="Start"/> to
/// <see cref="End"/>, which can leave a gap before the next line.</summary>
public sealed record TimedLyricLine(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Parses the steps of a lyrics lookup: the Lyrics tab of a <c>next</c>
/// response, then the <c>browse</c> response that tab opens, asked as the
/// Android app (timed) and as the web client (plain).
/// </summary>
public static class LyricsParser
{
    private const string LyricsPageType = "MUSIC_PAGE_TYPE_TRACK_LYRICS";

    /// <summary>
    /// The timed lyrics in a lyrics browse asked as the Android app, or null
    /// when it has none: no lyrics, lines without timings (many tracks have
    /// only plain lines there, which the web browse gives with their stanzas),
    /// or a shape this parser doesn't know. Every line must carry its timing,
    /// as ytmusicapi requires.
    /// </summary>
    public static TrackLyrics? ParseTimed(JsonElement root)
    {
        if (!root.TryGetPath(out var data,
                "contents", "elementRenderer", "newElement", "type", "componentType", "model",
                "timedLyricsModel", "lyricsData") ||
            !data.TryGetProperty("timedLyricsData", out var raw) ||
            raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() == 0)
        {
            return null;
        }

        var lines = new List<TimedLyricLine>(raw.GetArrayLength());
        foreach (var line in raw.EnumerateArray())
        {
            if (!line.TryGetProperty("lyricLine", out var text) || text.ValueKind != JsonValueKind.String ||
                !line.TryGetProperty("cueRange", out var cue) ||
                Milliseconds(cue, "startTimeMilliseconds") is not { } start ||
                Milliseconds(cue, "endTimeMilliseconds") is not { } end)
            {
                return null;
            }
            lines.Add(new TimedLyricLine(start, end < start ? start : end, text.GetString()!));
        }

        var credit = data.TryGetProperty("sourceMessage", out var source) && source.ValueKind == JsonValueKind.String
            ? source.GetString()
            : null;
        return new TrackLyrics(string.Join('\n', lines.Select(l => l.Text)), credit, lines);
    }

    // Written as strings ("26520"); a number is taken too.
    private static TimeSpan? Milliseconds(JsonElement cue, string property)
    {
        if (!cue.TryGetProperty(property, out var value))
            return null;
        long ms;
        if (value.ValueKind == JsonValueKind.String)
        {
            if (!long.TryParse(value.GetString(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out ms))
                return null;
        }
        else if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out ms) || ms < 0)
        {
            return null;
        }
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>
    /// The browse id of the watch page's Lyrics tab, or null when the page has no
    /// such tab. A tab marked <c>unselectable</c> still counts: its browse has
    /// been seen to return lyrics (an official music video's), so only the
    /// browse can say there are none.
    /// </summary>
    public static string? ParseBrowseId(JsonElement root)
    {
        if (!root.TryGetPath(out var tabs,
                "contents", "singleColumnMusicWatchNextResultsRenderer", "tabbedRenderer",
                "watchNextTabbedResultsRenderer", "tabs") ||
            tabs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // Found by page type, not position or title: the title is in the
        // request's language, and the tabs have moved before.
        foreach (var tab in tabs.EnumerateArray())
        {
            if (tab.TryGetPath(out var browse, "tabRenderer", "endpoint", "browseEndpoint") &&
                browse.TryGetPath(out var pageType,
                    "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType") &&
                pageType.ValueKind == JsonValueKind.String && pageType.GetString() == LyricsPageType &&
                browse.TryGetProperty("browseId", out var browseId) &&
                browseId.ValueKind == JsonValueKind.String && browseId.GetString() is { Length: > 0 } id)
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// The lyrics in a lyrics browse response, or null when it has none: YouTube
    /// Music answers a track without lyrics with a message ("Lyrics not
    /// available") in place of the shelf, and never says a track is instrumental.
    /// </summary>
    public static TrackLyrics? Parse(JsonElement root)
    {
        if (!root.TryGetPath(out var sections, "contents", "sectionListRenderer", "contents") ||
            sections.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var section in sections.EnumerateArray())
        {
            if (!section.TryGetProperty("musicDescriptionShelfRenderer", out var shelf) ||
                ParseHelpers.RunsText(shelf, "description") is not { } text ||
                string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            return new TrackLyrics(text, ParseHelpers.RunsText(shelf, "footer"));
        }

        return null;
    }
}
