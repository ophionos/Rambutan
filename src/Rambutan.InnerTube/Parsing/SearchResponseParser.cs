using System.Text.Json;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.InnerTube.Parsing;

/// <summary>
/// Parses youtubei/v1/search responses (WEB_REMIX) and search suggestions.
/// Pinned by recorded fixtures in tests/Rambutan.InnerTube.Tests/Fixtures — schema
/// drift must surface as a failing test, not a user report.
/// </summary>
public static class SearchResponseParser
{
    public static IReadOnlyList<TrackInfo> ParseSongs(JsonElement root) =>
        ParseEntries(root).OfType<SongEntry>().Select(e => e.Track).ToArray();

    /// <summary>
    /// Filtered search results as typed entries: watch items (songs/videos)
    /// become SongEntry; items with a browse endpoint become album/artist/
    /// playlist entries by page type.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> ParseEntries(JsonElement root) => ParsePage(root).Items;

    /// <summary>
    /// A filtered search's first page. The results shelf carries the next-page
    /// token in `continuations` (what ytmusicapi's search passes to
    /// get_continuations), or inline as a trailing continuationItemRenderer.
    /// </summary>
    public static SearchPage ParsePage(JsonElement root)
    {
        if (!root.TryGetPath(out var sections,
                "contents", "tabbedSearchResultsRenderer", "tabs", "0",
                "tabRenderer", "content", "sectionListRenderer", "contents"))
        {
            return SearchPage.Empty;
        }

        var entries = new List<CatalogEntry>();
        ContinuationToken? continuation = null;

        foreach (var section in sections.EnumerateArray())
        {
            if (section.TryGetProperty("musicShelfRenderer", out var shelf))
            {
                continuation = AppendShelf(shelf, entries) ?? continuation;
            }
        }

        return new SearchPage { Items = entries, Continuation = continuation };
    }

    /// <summary>
    /// A later page: continuationContents.musicShelfContinuation (ytmusicapi's
    /// get_continuations), or appended items in response actions/commands.
    /// </summary>
    public static SearchPage ParseContinuation(JsonElement root)
    {
        var entries = new List<CatalogEntry>();
        ContinuationToken? continuation = null;

        if (root.TryGetPath(out var shelf, "continuationContents", "musicShelfContinuation"))
        {
            continuation = AppendShelf(shelf, entries);
        }
        else
        {
            foreach (var key in new[] { "onResponseReceivedActions", "onResponseReceivedCommands" })
            {
                if (!root.TryGetProperty(key, out var actions) || actions.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var action in actions.EnumerateArray())
                {
                    if (action.TryGetPath(out var items, "appendContinuationItemsAction", "continuationItems"))
                        continuation = AppendItems(items, entries) ?? continuation;
                }
            }
        }

        return new SearchPage { Items = entries, Continuation = continuation };
    }

    private static ContinuationToken? AppendShelf(JsonElement shelf, List<CatalogEntry> entries)
    {
        var inline = shelf.TryGetProperty("contents", out var items) ? AppendItems(items, entries) : null;
        return inline ?? ParseHelpers.ParseContinuation(shelf);
    }

    private static ContinuationToken? AppendItems(JsonElement items, List<CatalogEntry> entries)
    {
        ContinuationToken? continuation = null;
        if (items.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("musicResponsiveListItemRenderer", out var renderer))
            {
                if (ParseHelpers.ParseListItemEntry(renderer) is { } entry)
                    entries.Add(entry);
            }
            else if (ParseHelpers.InlineContinuation(item) is { } token)
            {
                continuation = token;
            }
        }

        return continuation;
    }

    /// <summary>
    /// An unfiltered search: a musicCardShelfRenderer (the top result, what
    /// ytmusicapi's parse_top_result reads), then one itemSectionRenderer per
    /// result, each wrapping a single list row, with the types mixed in the
    /// server's ranking. (ytmusicapi and older YTM expected a titled
    /// musicShelfRenderer per type; live YTM stopped sending those.) The leading
    /// "About these results" messageRenderer section is skipped, and so are rows
    /// we can't open: podcast episodes and shows.
    /// </summary>
    public static SearchOverview ParseOverview(JsonElement root)
    {
        if (!root.TryGetPath(out var sections,
                "contents", "tabbedSearchResultsRenderer", "tabs", "0",
                "tabRenderer", "content", "sectionListRenderer", "contents") ||
            sections.ValueKind != JsonValueKind.Array)
        {
            return SearchOverview.Empty;
        }

        TopResult? top = null;
        var results = new List<CatalogEntry>();

        foreach (var section in sections.EnumerateArray())
        {
            if (section.TryGetProperty("musicCardShelfRenderer", out var card))
            {
                top ??= ParseTopResult(card);
            }
            else if (section.TryGetPath(out var items, "itemSectionRenderer", "contents") &&
                     items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("musicResponsiveListItemRenderer", out var renderer) &&
                        !IsPodcastEpisode(renderer) &&
                        ParseHelpers.ParseListItemEntry(renderer) is { } entry)
                    {
                        results.Add(entry);
                    }
                }
            }
        }

        return new SearchOverview { TopResult = top, Results = results };
    }

    /// <summary>An episode row plays like a song, but podcasts aren't something we play.</summary>
    private static bool IsPodcastEpisode(JsonElement renderer) =>
        renderer.TryGetPath(out var type,
            "overlay", "musicItemThumbnailOverlayRenderer", "content", "musicPlayButtonRenderer",
            "playNavigationEndpoint", "watchEndpoint", "watchEndpointMusicSupportedConfigs",
            "watchEndpointMusicConfig", "musicVideoType") &&
        type.ValueKind == JsonValueKind.String &&
        type.GetString() == "MUSIC_VIDEO_TYPE_PODCAST_EPISODE";

    private static TopResult? ParseTopResult(JsonElement card)
    {
        try
        {
            if (ParseCardEntry(card) is not { } entry)
                return null;

            var related = new List<CatalogEntry>();
            if (card.TryGetProperty("contents", out var contents))
                AppendItems(contents, related);   // skips the "More from YouTube" messageRenderer
            if (entry is ArtistEntry artist)
                CreditTheCardsArtist(related, new ArtistRef(artist.Title, artist.BrowseId));
            return new TopResult { Entry = entry, Related = related };
        }
        catch (Exception e) when (ParseHelpers.IsMalformedItem(e))
        {
            return null;
        }
    }

    /// <summary>
    /// An artist card's songs carry no artist runs ("Song • 5:21"): the card is
    /// the artist. They play, queue and scrobble with it, so they get it here.
    /// </summary>
    private static void CreditTheCardsArtist(List<CatalogEntry> related, ArtistRef artist)
    {
        for (var i = 0; i < related.Count; i++)
        {
            if (related[i] is SongEntry { Track.Artists.Count: 0 } song)
            {
                var track = song.Track with { Artists = [artist] };
                related[i] = song with { Track = track, Subtitle = track.ArtistDisplay };
            }
        }
    }

    /// <summary>
    /// The card's own entity. Artists, albums and playlists link from the title
    /// run (or onTap); songs and videos play from onTap's watchEndpoint, with
    /// artists and album in the subtitle runs after the type label.
    /// </summary>
    private static CatalogEntry? ParseCardEntry(JsonElement card)
    {
        if (!card.TryGetPath(out var titleRun, "title", "runs", "0") ||
            !titleRun.TryGetProperty("text", out var titleText) ||
            titleText.GetString() is not { Length: > 0 } title)
        {
            return null;
        }

        var subtitle = ParseHelpers.RunsText(card, "subtitle");
        var thumbnail = ParseHelpers.Thumbnail(card);

        if (card.TryGetPath(out var videoId, "onTap", "watchEndpoint", "videoId") &&
            videoId.GetString() is { Length: > 0 } watchId)
        {
            var artists = new List<ArtistRef>();
            string? album = null, albumBrowseId = null;
            TimeSpan? duration = null;
            if (card.TryGetPath(out var runs, "subtitle", "runs") && runs.ValueKind == JsonValueKind.Array)
            {
                foreach (var run in runs.EnumerateArray())
                {
                    var text = run.TryGetProperty("text", out var t) ? t.GetString() : null;
                    if (string.IsNullOrEmpty(text))
                        continue;
                    switch (ParseHelpers.PageType(run))
                    {
                        case "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL":
                            artists.Add(new ArtistRef(text, ParseHelpers.BrowseId(run)));
                            break;
                        case "MUSIC_PAGE_TYPE_ALBUM":
                            album = text;
                            albumBrowseId = ParseHelpers.BrowseId(run);
                            break;
                        default:
                            if (duration is null && ParseHelpers.TryParseDuration(text, out var d))
                                duration = d;
                            break;
                    }
                }
            }

            return new SongEntry
            {
                Title = title,
                Subtitle = subtitle,
                ThumbnailUrl = thumbnail,
                Track = new TrackInfo
                {
                    VideoId = watchId,
                    Title = title,
                    Artists = artists,
                    Album = album,
                    AlbumBrowseId = albumBrowseId,
                    Duration = duration,
                    ThumbnailUrl = thumbnail,
                },
            };
        }

        // Title run first; onTap carries the same browse target when the run doesn't.
        if (ParseHelpers.BrowseId(titleRun) is { } id)
            return ParseHelpers.BrowseEntry(ParseHelpers.PageType(titleRun), id, title, subtitle, thumbnail);

        if (card.TryGetPath(out var browse, "onTap", "browseEndpoint") &&
            browse.TryGetProperty("browseId", out var tapId) &&
            tapId.GetString() is { Length: > 0 } onTapId)
        {
            var pageType = browse.TryGetPath(out var type,
                "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType")
                ? type.GetString()
                : null;
            return ParseHelpers.BrowseEntry(pageType, onTapId, title, subtitle, thumbnail);
        }

        return null;
    }

    /// <summary>
    /// music/get_search_suggestions → query completions plus entity rows. The
    /// response is a list of searchSuggestionsSectionRenderer sections: the first
    /// holds text suggestions (history ones first when signed in), a later one the
    /// artists/songs/albums YTM matched outright. ytmusicapi's
    /// parse_search_suggestions reads only the first section; the entity section
    /// is the same list-item row search results use.
    /// </summary>
    public static SearchSuggestions ParseSuggestions(JsonElement root)
    {
        if (!root.TryGetProperty("contents", out var sections) ||
            sections.ValueKind != JsonValueKind.Array)
        {
            return SearchSuggestions.Empty;
        }

        var queries = new List<SearchSuggestion>();
        var entries = new List<CatalogEntry>();

        foreach (var section in sections.EnumerateArray())
        {
            if (!section.TryGetPath(out var items, "searchSuggestionsSectionRenderer", "contents") ||
                items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("searchSuggestionRenderer", out var text))
                {
                    if (SuggestionQuery(text) is { } query)
                        queries.Add(new SearchSuggestion(query));
                }
                else if (item.TryGetProperty("historySuggestionRenderer", out var history))
                {
                    if (SuggestionQuery(history) is { } query)
                        queries.Add(new SearchSuggestion(query, FromHistory: true));
                }
                else if (item.TryGetProperty("musicResponsiveListItemRenderer", out var renderer) &&
                         ParseHelpers.ParseListItemEntry(renderer) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        return new SearchSuggestions { Queries = queries, Entries = entries };
    }

    /// <summary>The query a suggestion runs: its search endpoint's, else its displayed text.</summary>
    private static string? SuggestionQuery(JsonElement suggestion) =>
        suggestion.TryGetPath(out var query, "navigationEndpoint", "searchEndpoint", "query") &&
        query.ValueKind == JsonValueKind.String &&
        query.GetString() is { Length: > 0 } value
            ? value
            : ParseHelpers.RunsText(suggestion, "suggestion");
}
