using System.Text.Json;
using Rambutan.Core.Models;

namespace Rambutan.InnerTube.Parsing;

internal static class ParseHelpers
{
    /// <summary>Walks nested properties; numeric segments index into arrays.</summary>
    public static bool TryGetPath(this JsonElement element, out JsonElement result, params string[] path)
    {
        result = element;
        foreach (var segment in path)
        {
            if (result.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index))
            {
                if (index >= result.GetArrayLength())
                    return false;
                result = result[index];
            }
            else if (result.ValueKind != JsonValueKind.Object ||
                     !result.TryGetProperty(segment, out result))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A malformed item — a renderer that matched but whose innards don't have the
    /// expected shape — is skipped, never fatal: one odd item must not blank a whole
    /// surface. Fixture drift still fails loudly in tests via the missing output.
    /// </summary>
    public static bool IsMalformedItem(Exception e) =>
        e is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException;

    /// <summary>Concatenated text of a `{"runs":[{"text":…}]}` object.</summary>
    public static string? RunsText(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var text) ? RunsText(text) : null;

    /// <summary>Concatenated text of the `runs` array under <paramref name="textObject"/>.</summary>
    public static string? RunsText(JsonElement textObject)
    {
        if (!textObject.TryGetPath(out var runs, "runs") || runs.ValueKind != JsonValueKind.Array)
            return null;

        var text = string.Concat(runs.EnumerateArray()
            .Select(r => r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null));
        return text.Length > 0 ? text : null;
    }

    /// <summary>Largest thumbnail URL under a musicThumbnailRenderer (or raw thumbnail object).</summary>
    public static string? Thumbnail(JsonElement renderer)
    {
        // `thumbnail…`, `thumbnailRenderer…` both occur; `foregroundThumbnail` is
        // the musicVisualHeaderRenderer (artist) variant.
        foreach (var root in new[] { "thumbnail", "thumbnailRenderer", "foregroundThumbnail" })
        {
            if (renderer.TryGetPath(out var thumbs,
                    root, "musicThumbnailRenderer", "thumbnail", "thumbnails") &&
                thumbs.ValueKind == JsonValueKind.Array &&
                thumbs.GetArrayLength() > 0 &&
                thumbs[thumbs.GetArrayLength() - 1].TryGetProperty("url", out var url) &&
                url.ValueKind == JsonValueKind.String)
            {
                return url.GetString();
            }
        }

        return null;
    }

    public static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = default;
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3)
            return false;

        var total = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var value) || value < 0)
                return false;
            total = total * 60 + value;
        }

        duration = TimeSpan.FromSeconds(total);
        return true;
    }

    // The type labels YouTube Music puts first in a card's subtitle. Requests
    // always ask for English (InnerTubeClient.Language), so these are fixed.
    private static readonly HashSet<string> TypeLabels =
        ["Song", "Video", "Episode", "Podcast", "Album", "Single", "EP", "Playlist", "Artist", "Profile"];

    /// <summary>
    /// Reads a card's plain subtitle runs ("Song • DJ Electrohead • 3:12") the
    /// way ytmusicapi does when no run links an artist: separators, a leading
    /// type label, view counts, years and the duration aren't artists; what's
    /// left is. The duration, when present, is returned too.
    /// </summary>
    public static List<ArtistRef> PlainSubtitleArtists(JsonElement runs, out TimeSpan? duration)
    {
        duration = null;
        var artists = new List<ArtistRef>();
        if (runs.ValueKind != JsonValueKind.Array)
            return artists;

        var first = true;
        foreach (var run in runs.EnumerateArray())
        {
            var text = run.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(text) || text is "•" or "·" or "&" or ",")
                continue;
            var leading = first;
            first = false;
            if (leading && TypeLabels.Contains(text))
                continue;
            if (TryParseDuration(text, out var d))
                duration ??= d;
            else if (!IsCountOrYear(text))
                artists.Add(new ArtistRef(text, BrowseId(run)));
        }
        return artists;
    }

    // The units a count ends in, in English like every request.
    private static readonly HashSet<string> CountUnits =
        ["view", "views", "play", "plays", "song", "songs", "track", "tracks",
         "episode", "episodes", "subscriber", "subscribers", "listener", "listeners"];

    // "674K views", "1.2M plays", "12 songs", "2013" — but not "50 Cent" or
    // "21 Savage": a number followed by any other word is a name.
    private static bool IsCountOrYear(string text)
    {
        if (text.Length == 4 && text.All(char.IsAsciiDigit))
            return true;
        var space = text.LastIndexOf(' ');
        return space > 0 && char.IsAsciiDigit(text[0]) && CountUnits.Contains(text[(space + 1)..]);
    }

    public static string? PageType(JsonElement navigationEndpointOwner)
    {
        return navigationEndpointOwner.TryGetPath(out var pageType,
            "navigationEndpoint", "browseEndpoint",
            "browseEndpointContextSupportedConfigs",
            "browseEndpointContextMusicConfig", "pageType")
            ? pageType.GetString()
            : null;
    }

    public static string? BrowseId(JsonElement navigationEndpointOwner)
    {
        return navigationEndpointOwner.TryGetPath(out var browseId,
                "navigationEndpoint", "browseEndpoint", "browseId") &&
            browseId.ValueKind == JsonValueKind.String
            ? browseId.GetString()
            : null;
    }

    private const string CreditsPrefix = "MPTC";

    /// <summary>
    /// The videoId behind a row's "View song credits" menu item (a TRACK_CREDITS
    /// browse to MPTC{videoId}): YTM's own name for the row's song. An audio-track
    /// row names itself; an album page's music-video row names its audio-track
    /// counterpart. Null when the row has no credits item. List rows and watch-queue
    /// panel rows share the menu shape.
    /// </summary>
    public static string? CreditsSongId(JsonElement renderer)
    {
        if (!renderer.TryGetPath(out var items, "menu", "menuRenderer", "items") ||
            items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("menuNavigationItemRenderer", out var navigation) &&
                PageType(navigation) == "MUSIC_PAGE_TYPE_TRACK_CREDITS" &&
                BrowseId(navigation) is { } browseId &&
                browseId.Length > CreditsPrefix.Length &&
                browseId.StartsWith(CreditsPrefix, StringComparison.Ordinal))
            {
                return browseId[CreditsPrefix.Length..];
            }
        }

        return null;
    }

    /// <summary>likeStatus value → Rating; null for anything unrecognised.</summary>
    public static Rating? ParseLikeStatus(JsonElement likeStatus) =>
        likeStatus.ValueKind != JsonValueKind.String ? null : likeStatus.GetString() switch
        {
            "LIKE" => Rating.Like,
            "DISLIKE" => Rating.Dislike,
            "INDIFFERENT" => Rating.None,
            _ => null,
        };

    /// <summary>
    /// A list row's rating: album, playlist and artist rows carry a
    /// likeButtonRenderer among the menu's top-level buttons. Search rows don't,
    /// so they come back null (unknown), never None.
    /// </summary>
    public static Rating? ListItemRating(JsonElement renderer)
    {
        if (!renderer.TryGetPath(out var buttons, "menu", "menuRenderer", "topLevelButtons") ||
            buttons.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var button in buttons.EnumerateArray())
        {
            if (button.TryGetPath(out var status, "likeButtonRenderer", "likeStatus"))
                return ParseLikeStatus(status);
        }

        return null;
    }

    /// <summary>
    /// A song row's "Save to library" toggle: a toggleMenuServiceItemRenderer in
    /// the row menu, whose default icon names the action the default endpoint
    /// takes (ytmusicapi's parse_song_menu_tokens). An "add" icon (BOOKMARK_BORDER,
    /// formerly LIBRARY_ADD) means the default endpoint adds and the toggled one
    /// removes, and isToggled says the song is saved; a "remove" icon (BOOKMARK,
    /// formerly LIBRARY_SAVED/LIBRARY_REMOVE) swaps them and means it is saved.
    /// Null when the row has no such item or either token is missing (signed out).
    /// </summary>
    public static LibraryToggle? ListItemLibraryToggle(JsonElement renderer)
    {
        if (!renderer.TryGetPath(out var items, "menu", "menuRenderer", "items") ||
            items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("toggleMenuServiceItemRenderer", out var toggle) ||
                !toggle.TryGetPath(out var icon, "defaultIcon", "iconType") ||
                icon.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var defaultToken = FeedbackToken(toggle, "defaultServiceEndpoint");
            var toggledToken = FeedbackToken(toggle, "toggledServiceEndpoint");
            if (defaultToken is null || toggledToken is null)
                continue;

            var isToggled = toggle.TryGetProperty("isToggled", out var t) && t.ValueKind == JsonValueKind.True;
            switch (icon.GetString())
            {
                case "BOOKMARK_BORDER" or "LIBRARY_ADD":
                    return new LibraryToggle(isToggled, AddToken: defaultToken, RemoveToken: toggledToken);
                case "BOOKMARK" or "LIBRARY_SAVED" or "LIBRARY_REMOVE":
                    return new LibraryToggle(true, AddToken: toggledToken, RemoveToken: defaultToken);
            }
        }

        return null;
    }

    private static string? FeedbackToken(JsonElement toggle, string endpoint) =>
        toggle.TryGetPath(out var token, endpoint, "feedbackEndpoint", "feedbackToken") &&
        token.ValueKind == JsonValueKind.String &&
        token.GetString() is { Length: > 0 } value
            ? value
            : null;

    /// <summary>musicResponsiveListItemRenderer → TrackInfo (search results, album/playlist rows, artist top songs).</summary>
    public static bool TryParseListItemTrack(JsonElement renderer, out TrackInfo track)
    {
        try
        {
            return TryParseListItemTrackCore(renderer, out track);
        }
        catch (Exception e) when (IsMalformedItem(e))
        {
            track = null!;
            return false;
        }
    }

    private static bool TryParseListItemTrackCore(JsonElement renderer, out TrackInfo track)
    {
        track = null!;

        if (!renderer.TryGetPath(out var columns, "flexColumns") || columns.GetArrayLength() == 0)
            return false;

        if (!columns[0].TryGetPath(out var titleRuns,
                "musicResponsiveListItemFlexColumnRenderer", "text", "runs") ||
            titleRuns.GetArrayLength() == 0)
        {
            return false;
        }

        var titleRun = titleRuns[0];
        var title = titleRun.TryGetProperty("text", out var t) ? t.GetString() : null;
        // Search suggestions put the watch target on the row, not the title run.
        var videoId = titleRun.TryGetPath(out var vid, "navigationEndpoint", "watchEndpoint", "videoId")
            ? vid.GetString()
            : renderer.TryGetPath(out var pid, "playlistItemData", "videoId") ? pid.GetString()
            : renderer.TryGetPath(out var rowVid, "navigationEndpoint", "watchEndpoint", "videoId")
                ? rowVid.GetString()
                : null;

        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(videoId))
            return false;

        var artists = new List<ArtistRef>();
        string? album = null;
        string? albumBrowseId = null;
        TimeSpan? duration = null;

        // Artists/album/duration live in the remaining flex columns, identified by
        // their browse page type (never by position — layouts vary per surface).
        for (var c = 1; c < columns.GetArrayLength(); c++)
        {
            if (!columns[c].TryGetPath(out var runs,
                    "musicResponsiveListItemFlexColumnRenderer", "text", "runs"))
            {
                continue;
            }

            foreach (var run in runs.EnumerateArray())
            {
                var text = run.TryGetProperty("text", out var rt) ? rt.GetString() : null;
                if (string.IsNullOrEmpty(text))
                    continue;

                switch (PageType(run))
                {
                    case "MUSIC_PAGE_TYPE_ARTIST":
                    case "MUSIC_PAGE_TYPE_USER_CHANNEL":
                        artists.Add(new ArtistRef(text, BrowseId(run)));
                        break;
                    case "MUSIC_PAGE_TYPE_ALBUM":
                        album = text;
                        albumBrowseId = BrowseId(run);
                        break;
                    default:
                        if (duration is null && TryParseDuration(text, out var parsed))
                            duration = parsed;
                        break;
                }
            }
        }

        // Fixed column (playlist/album rows) carries the authoritative duration.
        if (renderer.TryGetPath(out var fixedText,
                "fixedColumns", "0", "musicResponsiveListItemFixedColumnRenderer", "text") &&
            RunsText(fixedText) is { } fixedString &&
            TryParseDuration(fixedString, out var fixedDuration))
        {
            duration = fixedDuration;
        }

        track = new TrackInfo
        {
            VideoId = videoId,
            Title = title,
            Artists = artists,
            Album = album,
            AlbumBrowseId = albumBrowseId,
            Duration = duration,
            ThumbnailUrl = Thumbnail(renderer),
            SetVideoId = renderer.TryGetPath(out var setVideoId,
                "playlistItemData", "playlistSetVideoId")
                ? setVideoId.GetString()
                : null,
            Rating = ListItemRating(renderer),
            Library = ListItemLibraryToggle(renderer),
            SongId = CreditsSongId(renderer),
        };
        return true;
    }

    /// <summary>
    /// musicResponsiveListItemRenderer → CatalogEntry: rows with a browse target
    /// become album/artist/playlist entries by page type; watch rows become songs.
    /// Null for anything else (a malformed row, a podcast episode).
    /// </summary>
    public static CatalogEntry? ParseListItemEntry(JsonElement renderer)
    {
        if (ParseListItemBrowseEntry(renderer) is { } browseEntry)
            return browseEntry;

        return TryParseListItemTrack(renderer, out var track)
            ? new SongEntry
            {
                Title = track.Title,
                Subtitle = track.ArtistDisplay,
                ThumbnailUrl = track.ThumbnailUrl,
                Track = track,
            }
            : null;
    }

    private static CatalogEntry? ParseListItemBrowseEntry(JsonElement renderer)
    {
        try
        {
            return ParseListItemBrowseEntryCore(renderer);
        }
        catch (Exception e) when (IsMalformedItem(e))
        {
            return null;
        }
    }

    private static CatalogEntry? ParseListItemBrowseEntryCore(JsonElement renderer)
    {
        if (BrowseId(renderer) is not { } id)
            return null;

        var title = renderer.TryGetPath(out var col0,
            "flexColumns", "0", "musicResponsiveListItemFlexColumnRenderer", "text")
            ? RunsText(col0)
            : null;
        if (title is null)
            return null;

        var subtitle = renderer.TryGetPath(out var col1,
            "flexColumns", "1", "musicResponsiveListItemFlexColumnRenderer", "text")
            ? RunsText(col1)
            : null;

        return BrowseEntry(PageType(renderer), id, title, subtitle, Thumbnail(renderer));
    }

    /// <summary>A browse target → typed entry by page type; null for page types we don't open.</summary>
    public static CatalogEntry? BrowseEntry(
        string? pageType, string id, string title, string? subtitle, string? thumbnail) =>
        pageType switch
        {
            "MUSIC_PAGE_TYPE_ALBUM" => new AlbumEntry
            {
                Title = title, Subtitle = subtitle, ThumbnailUrl = thumbnail, BrowseId = id,
            },
            "MUSIC_PAGE_TYPE_PLAYLIST" => new PlaylistEntry
            {
                Title = title, Subtitle = subtitle, ThumbnailUrl = thumbnail,
                PlaylistId = id.StartsWith("VL", StringComparison.Ordinal) ? id[2..] : id,
            },
            "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL" => new ArtistEntry
            {
                Title = title, Subtitle = subtitle, ThumbnailUrl = thumbnail, BrowseId = id,
            },
            _ => null,
        };

    /// <summary>musicTwoRowItemRenderer → CatalogEntry (home/artist carousels).</summary>
    public static CatalogEntry? ParseTwoRowItem(JsonElement renderer)
    {
        try
        {
            return ParseTwoRowItemCore(renderer);
        }
        catch (Exception e) when (IsMalformedItem(e))
        {
            return null;
        }
    }

    private static CatalogEntry? ParseTwoRowItemCore(JsonElement renderer)
    {
        var title = RunsText(renderer, "title");
        if (title is null)
            return null;

        var subtitle = RunsText(renderer, "subtitle");
        var thumbnail = Thumbnail(renderer);

        if (renderer.TryGetPath(out var videoId,
                "navigationEndpoint", "watchEndpoint", "videoId") &&
            videoId.GetString() is { } watchId)
        {
            // The subtitle mixes artists with album/plays runs ("Artist • Album");
            // pick artist runs by page type so the whole line never lands in the
            // artist slot, falling back to the full subtitle when none carry one.
            var artists = new List<ArtistRef>();
            TimeSpan? duration = null;
            if (renderer.TryGetPath(out var subtitleRuns, "subtitle", "runs"))
            {
                foreach (var run in subtitleRuns.EnumerateArray())
                {
                    if (PageType(run) is "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL" &&
                        run.TryGetProperty("text", out var text) &&
                        text.GetString() is { Length: > 0 } name)
                    {
                        artists.Add(new ArtistRef(name, BrowseId(run)));
                    }
                }
                // No linked artist: read the plain runs, never the whole line
                // ("Song • DJ Electrohead" is a type label and an artist).
                var plain = PlainSubtitleArtists(subtitleRuns, out duration);
                if (artists.Count == 0)
                    artists = plain;
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
                    Duration = duration,
                    ThumbnailUrl = thumbnail,
                },
            };
        }

        if (!renderer.TryGetPath(out var browseId, "navigationEndpoint", "browseEndpoint", "browseId") ||
            browseId.GetString() is not { } id)
        {
            return null;
        }

        var entry = BrowseEntry(PageType(renderer), id, title, subtitle, thumbnail);
        return entry is AlbumEntry album
            ? album with { AudioPlaylistId = TwoRowAudioPlaylistId(renderer) }
            : entry;
    }

    /// <summary>
    /// An album card's own playlist: its play overlay's watchPlaylistEndpoint, else
    /// the menu's (ytmusicapi's MENU_PLAYLIST_ID); radio mixes (RD…) don't count.
    /// </summary>
    private static string? TwoRowAudioPlaylistId(JsonElement renderer)
    {
        if (renderer.TryGetPath(out var overlay,
                "thumbnailOverlay", "musicItemThumbnailOverlayRenderer", "content",
                "musicPlayButtonRenderer", "playNavigationEndpoint", "watchPlaylistEndpoint", "playlistId") &&
            overlay.GetString() is { Length: > 0 } fromOverlay &&
            !fromOverlay.StartsWith("RD", StringComparison.Ordinal))
        {
            return fromOverlay;
        }

        if (!renderer.TryGetPath(out var items, "menu", "menuRenderer", "items") ||
            items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetPath(out var id,
                    "menuNavigationItemRenderer", "navigationEndpoint", "watchPlaylistEndpoint", "playlistId") &&
                id.GetString() is { Length: > 0 } fromMenu &&
                !fromMenu.StartsWith("RD", StringComparison.Ordinal))
            {
                return fromMenu;
            }
        }

        return null;
    }

    /// <summary>A trailing continuationItemRenderer (the modern inline shape) → token.</summary>
    public static ContinuationToken? InlineContinuation(JsonElement item)
    {
        return item.TryGetPath(out var token,
                "continuationItemRenderer", "continuationEndpoint", "continuationCommand", "token") &&
            token.ValueKind == JsonValueKind.String
            ? new ContinuationToken(token.GetString()!)
            : null;
    }

    /// <summary>Section-list or shelf `continuations` array → token.</summary>
    public static ContinuationToken? ParseContinuation(JsonElement owner)
    {
        return owner.TryGetPath(out var token,
                "continuations", "0", "nextContinuationData", "continuation") &&
            token.ValueKind == JsonValueKind.String
            ? new ContinuationToken(token.GetString()!)
            : null;
    }
}
