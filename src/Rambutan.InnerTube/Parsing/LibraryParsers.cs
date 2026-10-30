using System.Text.Json;
using Rambutan.Core.Models;

namespace Rambutan.InnerTube.Parsing;

/// <summary>
/// Parsers for authed library surfaces (FEmusic_liked_playlists,
/// FEmusic_liked_albums, FEmusic_library_corpus_artists, FEmusic_liked_videos,
/// FEmusic_history)
/// and the mutation responses (playlist/create, browse/edit_playlist, feedback).
/// </summary>
public static class LibraryParsers
{
    /// <summary>FEmusic_liked_playlists → grid of playlists (+ Liked Music at the top).</summary>
    public static LibraryPage ParseLibraryPlaylists(JsonElement root) => ParseLibraryGrid(root);

    /// <summary>
    /// FEmusic_liked_albums → grid of albums, the same grid as playlists (ytmusicapi's
    /// parse_library_albums reads it with get_library_contents(response, GRID)).
    /// </summary>
    public static LibraryPage ParseLibraryAlbums(JsonElement root) => ParseLibraryGrid(root);

    /// <summary>
    /// FEmusic_library_corpus_artists → a musicShelfRenderer of artist rows
    /// (ytmusicapi's parse_library_artists: get_library_contents(response,
    /// MUSIC_SHELF), paged by musicShelfContinuation).
    /// </summary>
    public static LibraryPage ParseLibraryArtists(JsonElement root) =>
        LibraryShelf(root) is { } shelf ? ParseArtistShelf(shelf) : new LibraryPage { Items = [] };

    /// <summary>
    /// FEmusic_liked_videos → a musicShelfRenderer of song rows, paged like the
    /// artists list (ytmusicapi's parse_library_songs). The shelf can open with a
    /// "Shuffle all" row, which has no videoId and falls out as a non-track.
    /// </summary>
    public static LibraryPage ParseLibrarySongs(JsonElement root) =>
        LibraryShelf(root) is { } shelf ? ParseSongShelf(shelf) : new LibraryPage { Items = [] };

    /// <summary>
    /// FEmusic_history → one musicShelfRenderer per period ("Today", "Last week",
    /// a month), titled in the account's language; every row a song, newest
    /// first. A paused or empty history sends a musicNotifierShelfRenderer
    /// instead, which reads as no entries.
    /// </summary>
    public static LibraryPage ParseHistory(JsonElement root)
    {
        var items = new List<CatalogEntry>();
        if (!root.TryGetPath(out var sections,
                "contents", "singleColumnBrowseResultsRenderer", "tabs", "0",
                "tabRenderer", "content", "sectionListRenderer", "contents") ||
            sections.ValueKind != JsonValueKind.Array)
        {
            return new LibraryPage { Items = items };
        }

        foreach (var section in sections.EnumerateArray())
        {
            if (!section.TryGetProperty("musicShelfRenderer", out var shelf) ||
                ParseHelpers.RunsText(shelf, "title") is not { } played ||
                !shelf.TryGetProperty("contents", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var row in rows.EnumerateArray())
            {
                if (row.TryGetProperty("musicResponsiveListItemRenderer", out var renderer) &&
                    ParseHelpers.TryParseListItemTrack(renderer, out var track))
                {
                    items.Add(new HistoryEntry
                    {
                        Title = track.Title,
                        Subtitle = track.ArtistDisplay,
                        ThumbnailUrl = track.ThumbnailUrl,
                        Track = track,
                        Played = played,
                        RemoveToken = HistoryRemoveToken(renderer),
                    });
                }
            }
        }

        return new LibraryPage { Items = items };
    }

    /// <summary>
    /// The row menu's "Remove from history" feedback token, found by its icon:
    /// the text is in the account's language, and the item isn't the menu's last
    /// (ytmusicapi's get_history assumes it is; "Pin to Listen again" now follows it).
    /// </summary>
    private static string? HistoryRemoveToken(JsonElement renderer)
    {
        if (!renderer.TryGetPath(out var items, "menu", "menuRenderer", "items") ||
            items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("menuServiceItemRenderer", out var service) &&
                service.TryGetPath(out var icon, "icon", "iconType") &&
                icon.ValueKind == JsonValueKind.String &&
                icon.GetString() == "REMOVE_FROM_HISTORY" &&
                service.TryGetPath(out var token, "serviceEndpoint", "feedbackEndpoint", "feedbackToken") &&
                token.ValueKind == JsonValueKind.String &&
                token.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Continuation of the songs list (musicShelfContinuation or appended items).</summary>
    public static LibraryPage ParseLibrarySongsContinuation(JsonElement root)
    {
        if (root.TryGetPath(out var shelf, "continuationContents", "musicShelfContinuation"))
            return ParseSongShelf(shelf);

        var items = new List<CatalogEntry>();
        ContinuationToken? continuation = null;
        foreach (var appended in AppendedItems(root))
            continuation = AppendSongRows(appended, items) ?? continuation;
        return new LibraryPage { Items = items, Continuation = continuation };
    }

    /// <summary>
    /// feedback response → whether every token was applied
    /// (feedbackResponses[].isProcessed); null when the response doesn't say.
    /// </summary>
    public static bool? IsFeedbackProcessed(JsonElement root)
    {
        if (!root.TryGetProperty("feedbackResponses", out var responses) ||
            responses.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        bool? processed = null;
        foreach (var response in responses.EnumerateArray())
        {
            if (response.ValueKind != JsonValueKind.Object ||
                !response.TryGetProperty("isProcessed", out var flag))
            {
                continue;
            }
            if (flag.ValueKind == JsonValueKind.False)
                return false;
            if (flag.ValueKind == JsonValueKind.True)
                processed = true;
        }
        return processed;
    }

    /// <summary>The first musicShelfRenderer of a library tab, directly in a section
    /// or inside an itemSectionRenderer.</summary>
    private static JsonElement? LibraryShelf(JsonElement root)
    {
        if (root.TryGetPath(out var sections,
                "contents", "singleColumnBrowseResultsRenderer", "tabs", "0",
                "tabRenderer", "content", "sectionListRenderer", "contents") &&
            sections.ValueKind == JsonValueKind.Array)
        {
            foreach (var section in sections.EnumerateArray())
            {
                if (section.TryGetProperty("musicShelfRenderer", out var shelf) ||
                    section.TryGetPath(out shelf, "itemSectionRenderer", "contents", "0", "musicShelfRenderer"))
                {
                    return shelf;
                }
            }
        }

        return null;
    }

    private static LibraryPage ParseSongShelf(JsonElement shelf)
    {
        var items = new List<CatalogEntry>();
        var inline = shelf.TryGetProperty("contents", out var rows) ? AppendSongRows(rows, items) : null;
        return new LibraryPage
        {
            Items = items,
            Continuation = inline ?? ParseHelpers.ParseContinuation(shelf),
        };
    }

    private static ContinuationToken? AppendSongRows(JsonElement rows, List<CatalogEntry> items)
    {
        ContinuationToken? continuation = null;
        if (rows.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("musicResponsiveListItemRenderer", out var renderer))
            {
                if (ParseHelpers.TryParseListItemTrack(renderer, out var track))
                {
                    items.Add(new SongEntry
                    {
                        Title = track.Title,
                        Subtitle = track.ArtistDisplay,
                        ThumbnailUrl = track.ThumbnailUrl,
                        Track = track,
                    });
                }
            }
            else if (ParseHelpers.InlineContinuation(row) is { } token)
            {
                continuation = token;
            }
        }

        return continuation;
    }

    /// <summary>Continuation of the artists list (musicShelfContinuation or appended items).</summary>
    public static LibraryPage ParseLibraryArtistsContinuation(JsonElement root)
    {
        if (root.TryGetPath(out var shelf, "continuationContents", "musicShelfContinuation"))
            return ParseArtistShelf(shelf);

        var items = new List<CatalogEntry>();
        ContinuationToken? continuation = null;
        foreach (var appended in AppendedItems(root))
            continuation = AppendArtistRows(appended, items) ?? continuation;
        return new LibraryPage { Items = items, Continuation = continuation };
    }

    /// <summary>
    /// An artist's channel id from a browse id. Library rows can link the library's
    /// own view of an artist ("MPLA" + channel id); everywhere else wants the channel.
    /// </summary>
    public static string ChannelId(string browseId) =>
        browseId.StartsWith("MPLA", StringComparison.Ordinal) ? browseId[4..] : browseId;

    private static LibraryPage ParseArtistShelf(JsonElement shelf)
    {
        var items = new List<CatalogEntry>();
        var inline = shelf.TryGetProperty("contents", out var rows) ? AppendArtistRows(rows, items) : null;
        return new LibraryPage
        {
            Items = items,
            Continuation = inline ?? ParseHelpers.ParseContinuation(shelf),
        };
    }

    private static ContinuationToken? AppendArtistRows(JsonElement rows, List<CatalogEntry> items)
    {
        ContinuationToken? continuation = null;
        if (rows.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("musicResponsiveListItemRenderer", out var renderer))
            {
                if (ParseArtistRow(renderer) is { } artist)
                    items.Add(artist);
            }
            else if (ParseHelpers.InlineContinuation(row) is { } token)
            {
                continuation = token;
            }
        }

        return continuation;
    }

    /// <summary>One artist row: name, "1.2M subscribers", art; any artist-ish link.</summary>
    private static ArtistEntry? ParseArtistRow(JsonElement renderer)
    {
        if (ParseHelpers.BrowseId(renderer) is not { } id ||
            ParseHelpers.PageType(renderer) is not
                ("MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL" or "MUSIC_PAGE_TYPE_LIBRARY_ARTIST") ||
            !renderer.TryGetPath(out var name,
                "flexColumns", "0", "musicResponsiveListItemFlexColumnRenderer", "text") ||
            ParseHelpers.RunsText(name) is not { } title)
        {
            return null;
        }

        return new ArtistEntry
        {
            Title = title,
            Subtitle = renderer.TryGetPath(out var detail,
                "flexColumns", "1", "musicResponsiveListItemFlexColumnRenderer", "text")
                ? ParseHelpers.RunsText(detail)
                : null,
            ThumbnailUrl = ParseHelpers.Thumbnail(renderer),
            BrowseId = ChannelId(id),
        };
    }

    private static LibraryPage ParseLibraryGrid(JsonElement root)
    {
        if (root.TryGetPath(out var sections,
                "contents", "singleColumnBrowseResultsRenderer", "tabs", "0",
                "tabRenderer", "content", "sectionListRenderer", "contents"))
        {
            foreach (var section in sections.EnumerateArray())
            {
                // The grid sits directly in the section or inside an itemSectionRenderer.
                if (section.TryGetProperty("gridRenderer", out var grid) ||
                    section.TryGetPath(out grid, "itemSectionRenderer", "contents", "0", "gridRenderer"))
                {
                    return ParseGrid(grid);
                }
            }
        }

        return new LibraryPage { Items = [] };
    }

    /// <summary>Continuation of a library grid, playlists or albums (gridContinuation or appended items).</summary>
    public static LibraryPage ParseLibraryGridContinuation(JsonElement root)
    {
        if (root.TryGetPath(out var grid, "continuationContents", "gridContinuation"))
            return ParseGrid(grid);

        // Modern shape: appended items arrive as response actions.
        var items = new List<CatalogEntry>();
        ContinuationToken? continuation = null;
        foreach (var appended in AppendedItems(root))
            continuation = AppendGridItems(appended, items) ?? continuation;

        return new LibraryPage { Items = items, Continuation = continuation };
    }

    /// <summary>The playlists grid's continuation; the same shape as any library grid.</summary>
    public static LibraryPage ParseLibraryPlaylistsContinuation(JsonElement root) =>
        ParseLibraryGridContinuation(root);

    /// <summary>onResponseReceivedActions[].appendContinuationItemsAction.continuationItems arrays.</summary>
    private static IEnumerable<JsonElement> AppendedItems(JsonElement root)
    {
        if (!root.TryGetProperty("onResponseReceivedActions", out var actions) ||
            actions.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var action in actions.EnumerateArray())
        {
            if (action.TryGetPath(out var appended, "appendContinuationItemsAction", "continuationItems") &&
                appended.ValueKind == JsonValueKind.Array)
            {
                yield return appended;
            }
        }
    }

    /// <summary>playlist/create response → new playlistId.</summary>
    public static string ParseCreatedPlaylistId(JsonElement root)
    {
        return root.TryGetProperty("playlistId", out var id) && id.GetString() is { } value
            ? value
            : throw new InvalidOperationException(
                "playlist/create response contained no playlistId.");
    }

    /// <summary>browse/edit_playlist response → success flag ("status": "STATUS_SUCCEEDED").</summary>
    public static bool IsEditSuccess(JsonElement root)
    {
        return root.TryGetProperty("status", out var status) &&
               status.GetString() == "STATUS_SUCCEEDED";
    }

    /// <summary>
    /// browse/edit_playlist response to an ACTION_ADD_VIDEO → the new row's setVideoId
    /// (playlistEditResults[].playlistEditVideoAddedResultData.setVideoId), or null.
    /// </summary>
    public static string? ParseAddedSetVideoId(JsonElement root)
    {
        if (!root.TryGetProperty("playlistEditResults", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("playlistEditVideoAddedResultData", out var added) &&
                added.ValueKind == JsonValueKind.Object &&
                added.TryGetProperty("setVideoId", out var id) &&
                id.ValueKind == JsonValueKind.String)
            {
                return id.GetString();
            }
        }
        return null;
    }

    private static LibraryPage ParseGrid(JsonElement grid)
    {
        var items = new List<CatalogEntry>();
        ContinuationToken? continuation = null;

        if (grid.TryGetProperty("items", out var gridItems))
            continuation = AppendGridItems(gridItems, items);

        return new LibraryPage
        {
            Items = items,
            Continuation = continuation ?? ParseHelpers.ParseContinuation(grid),
        };
    }

    /// <summary>
    /// Appends two-row entries; returns an inline continuation token if present.
    /// The "New playlist" musicNavigationButtonRenderer has no browse target and
    /// falls out naturally.
    /// </summary>
    private static ContinuationToken? AppendGridItems(JsonElement gridItems, List<CatalogEntry> items)
    {
        ContinuationToken? continuation = null;

        foreach (var item in gridItems.EnumerateArray())
        {
            if (item.TryGetProperty("musicTwoRowItemRenderer", out var twoRow))
            {
                if (ParseHelpers.ParseTwoRowItem(twoRow) is { } entry)
                    items.Add(entry);
            }
            else if (item.TryGetPath(out var token,
                         "continuationItemRenderer", "continuationEndpoint",
                         "continuationCommand", "token") &&
                     token.ValueKind == JsonValueKind.String)
            {
                continuation = new ContinuationToken(token.GetString()!);
            }
        }

        return continuation;
    }
}
