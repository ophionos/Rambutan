using System.Text.Json;

namespace Rambutan.InnerTube.Parsing;

/// <summary>A track's lyrics as YouTube Music shows them: plain text, no timing.</summary>
/// <param name="Credit">YouTube Music's own line naming where the text came from
/// ("Source: Musixmatch"), as it wrote it; null when it gave none.</param>
public sealed record TrackLyrics(string Text, string? Credit);

/// <summary>
/// Parses the two steps of a lyrics lookup: the Lyrics tab of a <c>next</c>
/// response, then the <c>browse</c> response that tab opens.
/// </summary>
public static class LyricsParser
{
    private const string LyricsPageType = "MUSIC_PAGE_TYPE_TRACK_LYRICS";

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
