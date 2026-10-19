using System.Globalization;
using System.Text.Json;
using Rambutan.Core.Models;

namespace Rambutan.InnerTube.Parsing;

/// <summary>
/// Parsers for youtubei/v1/browse responses. Each is pinned by a recorded fixture
/// in tests/Rambutan.InnerTube.Tests/Fixtures.
/// </summary>
public static class BrowseParsers
{
    public static HomePage ParseHome(JsonElement root) =>
        root.TryGetPath(out var list,
            "contents", "singleColumnBrowseResultsRenderer", "tabs", "0",
            "tabRenderer", "content", "sectionListRenderer")
            ? ParseSectionList(list)
            : HomePage.Empty;

    /// <summary>
    /// More shelves: continuationContents.sectionListContinuation, the same
    /// contents and continuations as the first page's sectionListRenderer.
    /// </summary>
    public static HomePage ParseHomeContinuation(JsonElement root) =>
        root.TryGetPath(out var list, "continuationContents", "sectionListContinuation")
            ? ParseSectionList(list)
            : HomePage.Empty;

    private static HomePage ParseSectionList(JsonElement list)
    {
        var shelves = new List<Shelf>();
        if (list.TryGetProperty("contents", out var sections))
        {
            foreach (var section in sections.EnumerateArray())
            {
                if ((ParseCarousel(section) ?? ParseGrid(section)) is { } shelf)
                    shelves.Add(shelf);
            }
        }

        var token = list.TryGetPath(out var next,
            "continuations", "0", "nextContinuationData", "continuation")
            ? next.GetString()
            : null;

        return new HomePage
        {
            Shelves = shelves,
            Continuation = string.IsNullOrEmpty(token) ? null : new ContinuationToken(token),
            Chips = ParseChips(list),
        };
    }

    /// <summary>
    /// The chip cloud above Home's shelves. The Podcasts chip is left out, as
    /// podcast shelves are: there is nothing here to open them with.
    /// </summary>
    private static List<HomeChip> ParseChips(JsonElement list)
    {
        var chips = new List<HomeChip>();
        if (!list.TryGetPath(out var cloud, "header", "chipCloudRenderer", "chips"))
            return chips;

        foreach (var item in cloud.EnumerateArray())
        {
            if (!item.TryGetProperty("chipCloudChipRenderer", out var chip) ||
                ParseHelpers.RunsText(chip, "text") is not { } title ||
                !chip.TryGetPath(out var param, "navigationEndpoint", "browseEndpoint", "params") ||
                param.GetString() is not { Length: > 0 } chipParams ||
                IsPodcastChip(chipParams))
                continue;

            var selected = chip.TryGetProperty("isSelected", out var flag) && flag.ValueKind == JsonValueKind.True;
            chips.Add(new HomeChip(title, chipParams, selected));
        }
        return chips;
    }

    /// <summary>
    /// A chip's params are a protobuf that lists every chip as an (id, state)
    /// pair, where state 3 is the chip it selects; the Podcasts chip is id 12.
    /// Read this way rather than by title, which follows the request's language.
    /// </summary>
    public static bool IsPodcastChip(string chipParams)
    {
        byte[] bytes;
        try
        {
            var base64 = Uri.UnescapeDataString(chipParams).Replace('-', '+').Replace('_', '/');
            bytes = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
        }
        catch (FormatException)
        {
            return false;
        }

        // Each pair: 0x4A 0x04 (field 9, 4 bytes), then 0x08 id, 0x10 state.
        ReadOnlySpan<byte> selectsPodcasts = [0x4A, 0x04, 0x08, 0x0C, 0x10, 0x03];
        return bytes.AsSpan().IndexOf(selectsPodcasts) >= 0;
    }

    /// <summary>
    /// The Moods &amp; genres grid: a gridRenderer per group ("Moods &amp; moments",
    /// "Genres") of musicNavigationButtonRenderer tiles, each opening
    /// FEmusic_moods_and_genres_category with its own params.
    /// </summary>
    public static MoodsPage ParseMoodsAndGenres(JsonElement root)
    {
        if (!root.TryGetPath(out var sections,
                "contents", "singleColumnBrowseResultsRenderer", "tabs", "0",
                "tabRenderer", "content", "sectionListRenderer", "contents"))
            return MoodsPage.Empty;

        var groups = new List<MoodSection>();
        foreach (var section in sections.EnumerateArray())
        {
            if (!section.TryGetProperty("gridRenderer", out var grid) ||
                !grid.TryGetProperty("items", out var items))
                continue;

            var title = grid.TryGetPath(out var header, "header", "gridHeaderRenderer")
                ? ParseHelpers.RunsText(header, "title")
                : null;

            var categories = new List<MoodCategory>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("musicNavigationButtonRenderer", out var button) ||
                    ParseHelpers.RunsText(button, "buttonText") is not { } name ||
                    CategoryParams(button, "clickCommand") is not { } categoryParams)
                    continue;

                var color = button.TryGetPath(out var stripe, "solid", "leftStripeColor") &&
                            stripe.TryGetUInt32(out var argb)
                    ? argb
                    : (uint?)null;
                categories.Add(new MoodCategory(name, categoryParams) { Color = color });
            }

            if (title is not null && categories.Count > 0)
                groups.Add(new MoodSection(title, categories));
        }

        return new MoodsPage { Sections = groups };
    }

    /// <summary>
    /// A mood or genre page: Home's shelf shapes (carousels, and a grid for a
    /// "Spotlight"), under a musicHeaderRenderer title.
    /// </summary>
    public static MoodCategoryPage ParseMoodCategory(JsonElement root)
    {
        var title = root.TryGetPath(out var header, "header", "musicHeaderRenderer")
            ? ParseHelpers.RunsText(header, "title")
            : null;
        return new MoodCategoryPage { Title = title, Shelves = ParseHome(root).Shelves };
    }

    internal const string MoodCategoryBrowseId = "FEmusic_moods_and_genres_category";

    /// <summary>The params of a browseEndpoint that opens a mood or genre page, else null.</summary>
    private static string? CategoryParams(JsonElement owner, string endpointProperty) =>
        owner.TryGetPath(out var endpoint, endpointProperty, "browseEndpoint") &&
        endpoint.TryGetProperty("browseId", out var id) &&
        id.GetString() == MoodCategoryBrowseId &&
        endpoint.TryGetProperty("params", out var param) &&
        param.GetString() is { Length: > 0 } value
            ? value
            : null;

    private static Shelf? ParseCarousel(JsonElement section)
    {
        if (!section.TryGetProperty("musicCarouselShelfRenderer", out var carousel))
            return null;

        var title = carousel.TryGetPath(out var header,
            "header", "musicCarouselShelfBasicHeaderRenderer")
            ? ParseHelpers.RunsText(header, "title")
            : null;

        if (title is null || !carousel.TryGetProperty("contents", out var items))
            return null;

        var entries = new List<CatalogEntry>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("musicTwoRowItemRenderer", out var twoRow))
            {
                if (ParseHelpers.ParseTwoRowItem(twoRow) is { } entry)
                    entries.Add(entry);
            }
            else if (item.TryGetProperty("musicResponsiveListItemRenderer", out var listItem) &&
                     ParseHelpers.TryParseListItemTrack(listItem, out var track))
            {
                entries.Add(new SongEntry
                {
                    Title = track.Title,
                    Subtitle = track.ArtistDisplay,
                    ThumbnailUrl = track.ThumbnailUrl,
                    Track = track,
                });
            }
        }

        var more = carousel.TryGetPath(out var button, "header", "musicCarouselShelfBasicHeaderRenderer",
                "moreContentButton", "buttonRenderer") &&
            CategoryParams(button, "navigationEndpoint") is { } moreParams
            ? new MoodCategory(title, moreParams)
            : null;

        return entries.Count > 0 ? new Shelf(title, entries) { More = more } : null;
    }

    /// <summary>A gridRenderer of cards (a mood page's "Spotlight"), as one shelf.</summary>
    private static Shelf? ParseGrid(JsonElement section)
    {
        if (!section.TryGetProperty("gridRenderer", out var grid) ||
            !grid.TryGetProperty("items", out var items) ||
            !grid.TryGetPath(out var header, "header", "gridHeaderRenderer") ||
            ParseHelpers.RunsText(header, "title") is not { } title)
            return null;

        var entries = new List<CatalogEntry>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("musicTwoRowItemRenderer", out var twoRow) &&
                ParseHelpers.ParseTwoRowItem(twoRow) is { } entry)
                entries.Add(entry);
        }

        return entries.Count > 0 ? new Shelf(title, entries) : null;
    }

    public static AlbumPage ParseAlbum(string browseId, JsonElement root)
    {
        var header = TwoColumnHeader(root);
        var title = header is { } h ? ParseHelpers.RunsText(h, "title") : null;
        var artist = header is { } h2 ? ParseHelpers.RunsText(h2, "straplineTextOne") : null;
        // The strapline's first run links the album artist; row fallbacks reuse it.
        var artistBrowseId = header is { } h6 &&
            h6.TryGetPath(out var straplineRun, "straplineTextOne", "runs", "0")
            ? ParseHelpers.BrowseId(straplineRun)
            : null;
        var thumbnail = header is { } h5 ? ParseHelpers.Thumbnail(h5) : null;

        var tracks = new List<TrackInfo>();
        if (root.TryGetPath(out var sections,
                "contents", "twoColumnBrowseResultsRenderer", "secondaryContents",
                "sectionListRenderer", "contents"))
        {
            foreach (var section in sections.EnumerateArray())
            {
                if (!section.TryGetProperty("musicShelfRenderer", out var shelf) ||
                    !shelf.TryGetProperty("contents", out var items))
                {
                    continue;
                }

                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("musicResponsiveListItemRenderer", out var renderer) &&
                        ParseHelpers.TryParseListItemTrack(renderer, out var track))
                    {
                        // Album rows omit what the page header already states.
                        tracks.Add(track with
                        {
                            TrackNumber = TrackNumber(renderer),
                            Album = track.Album ?? title,
                            AlbumBrowseId = track.AlbumBrowseId ?? browseId,
                            // Rows carry no art of their own; the queue and the
                            // player bar show the album's.
                            ThumbnailUrl = track.ThumbnailUrl ?? thumbnail,
                            Artists = track.Artists.Count > 0 || artist is null
                                ? track.Artists
                                : [new ArtistRef(artist, artistBrowseId)],
                        });
                    }
                }
            }
        }

        return new AlbumPage
        {
            BrowseId = browseId,
            Title = title ?? "(unknown album)",
            Artist = artist,
            ArtistBrowseId = artistBrowseId,
            SubtitleInfo = header is { } h3 ? ParseHelpers.RunsText(h3, "subtitle") : null,
            SecondSubtitle = header is { } h4 ? ParseHelpers.RunsText(h4, "secondSubtitle") : null,
            ThumbnailUrl = thumbnail,
            AudioPlaylistId = header is { } h7 ? HeaderButton(h7, "musicPlayButtonRenderer") is { } play
                ? AudioPlaylistId(play) : null : null,
            InLibrary = header is { } h8 &&
                HeaderButton(h8, "toggleButtonRenderer") is { } toggle &&
                toggle.TryGetProperty("isToggled", out var toggled) &&
                toggled.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? toggled.GetBoolean()
                : null,
            Tracks = tracks,
        };
    }

    /// <summary>The first header button of a renderer kind (ytmusicapi's find_object_by_key).</summary>
    private static JsonElement? HeaderButton(JsonElement header, string kind)
    {
        if (!header.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var button in buttons.EnumerateArray())
        {
            if (button.TryGetProperty(kind, out var renderer))
                return renderer;
        }
        return null;
    }

    /// <summary>The play button's playlist (watchPlaylistEndpoint, or watchEndpoint in an A/B variant).</summary>
    private static string? AudioPlaylistId(JsonElement playButton)
    {
        foreach (var endpoint in new[] { "watchPlaylistEndpoint", "watchEndpoint" })
        {
            if (playButton.TryGetPath(out var id, "playNavigationEndpoint", endpoint, "playlistId") &&
                id.ValueKind == JsonValueKind.String)
            {
                return id.GetString();
            }
        }
        return null;
    }

    /// <summary>An album row's printed number ("index" runs); null when absent or not a number.</summary>
    private static int? TrackNumber(JsonElement renderer) =>
        int.TryParse(ParseHelpers.RunsText(renderer, "index"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;

    public static PlaylistPage ParsePlaylist(string playlistId, JsonElement root)
    {
        var header = TwoColumnHeader(root);
        var editable = IsEditablePlaylist(root);

        var tracks = new List<TrackInfo>();
        ContinuationToken? continuation = null;

        if (root.TryGetPath(out var sectionList,
                "contents", "twoColumnBrowseResultsRenderer", "secondaryContents",
                "sectionListRenderer"))
        {
            continuation = ParseHelpers.ParseContinuation(sectionList);

            if (sectionList.TryGetProperty("contents", out var sections))
            {
                foreach (var section in sections.EnumerateArray())
                {
                    if (!section.TryGetProperty("musicPlaylistShelfRenderer", out var shelf))
                        continue;

                    // An inline continuationItemRenderer (modern) outranks the
                    // section-level token, which only pages "related" content.
                    continuation = AppendShelfTracks(shelf, tracks)
                                   ?? ParseHelpers.ParseContinuation(shelf)
                                   ?? continuation;
                }
            }
        }

        return new PlaylistPage
        {
            PlaylistId = playlistId,
            Title = (header is { } h ? ParseHelpers.RunsText(h, "title") : null) ?? "(unknown playlist)",
            SubtitleInfo = header is { } h2 ? ParseHelpers.RunsText(h2, "subtitle") : null,
            SecondSubtitle = header is { } h3 ? ParseHelpers.RunsText(h3, "secondSubtitle") : null,
            Author = header is { } h6 ? PlaylistAuthor(h6) : null,
            // An owned playlist's subtitle names its privacy first: "Playlist • Private • 2026".
            Year = header is { } h7 &&
                   h7.TryGetPath(out var year, "subtitle", "runs", editable ? "4" : "2", "text") &&
                   int.TryParse(year.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var y) &&
                   y is > 1900 and < 3000
                ? y
                : null,
            TrackCount = header is { } h8 ? PlaylistCounts(h8).TrackCount : null,
            DurationText = header is { } h9 ? PlaylistCounts(h9).Duration : null,
            Description = header is { } h4 ? ParseHelpers.RunsText(h4, "description") : null,
            ThumbnailUrl = header is { } h5 ? ParseHelpers.Thumbnail(h5) : null,
            Tracks = tracks,
            Continuation = continuation,
            IsEditable = editable,
        };
    }

    /// <summary>
    /// The header's author (ytmusicapi's parse_playlist_header_meta): the facepile's
    /// name and, when it links one, the channel its tap opens. A facepile whose tap
    /// opens the collaborate panel lists collaborators, not an author. Older headers
    /// put the author in straplineTextOne instead.
    /// </summary>
    private static ArtistRef? PlaylistAuthor(JsonElement header)
    {
        if (header.TryGetPath(out var stack, "facepile", "avatarStackViewModel"))
        {
            var command = stack.TryGetPath(out var c,
                "rendererContext", "commandContext", "onTap", "innertubeCommand") ? c : (JsonElement?)null;
            if (command is { } cmd &&
                cmd.TryGetPath(out var tag, "showEngagementPanelEndpoint", "identifier", "tag") &&
                tag.GetString() == "PAplaylist_collaborate")
            {
                return null;
            }

            if (stack.TryGetPath(out var name, "text", "content") &&
                name.GetString() is { Length: > 0 } authorName)
            {
                var id = command is { } withId &&
                         withId.TryGetPath(out var browseId, "browseEndpoint", "browseId") &&
                         browseId.ValueKind == JsonValueKind.String
                    ? browseId.GetString()
                    : null;
                return new ArtistRef(authorName, id);
            }
        }

        if (header.TryGetPath(out var strapline, "straplineTextOne", "runs", "0") &&
            strapline.TryGetProperty("text", out var text) &&
            text.GetString() is { Length: > 0 } straplineName)
        {
            return new ArtistRef(straplineName, ParseHelpers.BrowseId(strapline));
        }

        return null;
    }

    /// <summary>
    /// secondSubtitle → track count and duration, laid out as ytmusicapi reads it:
    /// "101 songs • 7+ hours", or with views first ("1.2M views • 101 songs • 7+ hours")
    /// when there are more than three runs.
    /// </summary>
    private static (int? TrackCount, string? Duration) PlaylistCounts(JsonElement header)
    {
        if (!header.TryGetPath(out var runs, "secondSubtitle", "runs") ||
            runs.ValueKind != JsonValueKind.Array || runs.GetArrayLength() == 0)
        {
            return (null, null);
        }

        var count = runs.GetArrayLength();
        var countAt = count > 3 ? 2 : 0;
        string? Text(int i) =>
            i < count && runs[i].TryGetProperty("text", out var t) ? t.GetString() : null;

        var digits = new string((Text(countAt) ?? "").Where(char.IsAsciiDigit).ToArray());
        int? tracks = digits.Length > 0 &&
                      int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
        return (tracks, count > 1 ? Text(countAt + 2) : null);
    }

    /// <summary>
    /// Continuation responses carry either musicPlaylistShelfContinuation or a
    /// sectionListContinuation whose sections may include more playlist shelves
    /// (track-less "related" carousels simply contribute nothing).
    /// </summary>
    public static PlaylistPage ParsePlaylistContinuation(string playlistId, JsonElement root)
    {
        var tracks = new List<TrackInfo>();
        ContinuationToken? continuation = null;

        // Modern shape: appended items arrive as response actions.
        if (root.TryGetPath(out var actions, "onResponseReceivedActions"))
        {
            foreach (var action in actions.EnumerateArray())
            {
                if (action.TryGetPath(out var items,
                        "appendContinuationItemsAction", "continuationItems"))
                {
                    continuation = AppendItems(items, tracks) ?? continuation;
                }
            }
        }
        else if (root.TryGetPath(out var shelfContinuation,
                     "continuationContents", "musicPlaylistShelfContinuation"))
        {
            continuation = AppendShelfTracks(shelfContinuation, tracks)
                           ?? ParseHelpers.ParseContinuation(shelfContinuation);
        }
        else if (root.TryGetPath(out var sectionContinuation,
                     "continuationContents", "sectionListContinuation"))
        {
            continuation = ParseHelpers.ParseContinuation(sectionContinuation);
            if (sectionContinuation.TryGetProperty("contents", out var sections))
            {
                foreach (var section in sections.EnumerateArray())
                {
                    if (section.TryGetProperty("musicPlaylistShelfRenderer", out var shelf))
                        continuation = AppendShelfTracks(shelf, tracks) ?? continuation;
                }
            }
        }

        return new PlaylistPage
        {
            PlaylistId = playlistId,
            Title = "",
            Tracks = tracks,
            Continuation = continuation,
        };
    }

    public static ArtistPage ParseArtist(string browseId, JsonElement root)
    {
        string? name = null;
        string? description = null;
        string? thumbnail = null;
        bool? subscribed = null;
        MixSeed? mix = null;
        MixSeed? shuffle = null;

        if (root.TryGetPath(out var header, "header", "musicImmersiveHeaderRenderer") ||
            root.TryGetPath(out header, "header", "musicVisualHeaderRenderer"))
        {
            name = ParseHelpers.RunsText(header, "title");
            description = ParseHelpers.RunsText(header, "description");
            thumbnail = ParseHelpers.Thumbnail(header);
            if (header.TryGetPath(out var state,
                    "subscriptionButton", "subscribeButtonRenderer", "subscribed") &&
                state.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                subscribed = state.GetBoolean();
            }
            mix = HeaderMix(header, "startRadioButton");
            shuffle = HeaderMix(header, "playButton");
        }

        var topSongs = new List<TrackInfo>();
        var shelves = new List<Shelf>();

        if (root.TryGetPath(out var sections,
                "contents", "singleColumnBrowseResultsRenderer", "tabs", "0",
                "tabRenderer", "content", "sectionListRenderer", "contents"))
        {
            foreach (var section in sections.EnumerateArray())
            {
                if (section.TryGetProperty("musicShelfRenderer", out var songShelf) &&
                    songShelf.TryGetProperty("contents", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("musicResponsiveListItemRenderer", out var renderer) &&
                            ParseHelpers.TryParseListItemTrack(renderer, out var track))
                        {
                            topSongs.Add(track);
                        }
                    }
                }
                else if (ParseCarousel(section) is { } shelf)
                {
                    shelves.Add(shelf);
                }
            }
        }

        return new ArtistPage
        {
            BrowseId = browseId,
            Name = name ?? "(unknown artist)",
            Description = description,
            ThumbnailUrl = thumbnail,
            Subscribed = subscribed,
            TopSongs = topSongs,
            Shelves = shelves,
            Mix = mix,
            Shuffle = shuffle,
        };
    }

    /// <summary>The mix a header button starts: <c>startRadioButton</c> is its Mix
    /// (the artist's radio), <c>playButton</c> its Shuffle (the artist's own songs).</summary>
    private static MixSeed? HeaderMix(JsonElement header, string button)
    {
        if (!header.TryGetPath(out var watch,
                button, "buttonRenderer", "navigationEndpoint", "watchEndpoint") ||
            Text(watch, "videoId") is not { Length: > 0 } videoId ||
            Text(watch, "playlistId") is not { Length: > 0 } playlistId)
        {
            return null;
        }
        return new MixSeed(videoId, playlistId, Text(watch, "params"));

        static string? Text(JsonElement owner, string property) =>
            owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>
    /// The page's header. A playlist the account owns wraps it in an editable header
    /// (ytmusicapi's get_playlist reads it from either place).
    /// </summary>
    private static JsonElement? TwoColumnHeader(JsonElement root)
    {
        if (!root.TryGetPath(out var first,
                "contents", "twoColumnBrowseResultsRenderer", "tabs", "0",
                "tabRenderer", "content", "sectionListRenderer", "contents", "0"))
        {
            return null;
        }
        if (first.TryGetProperty("musicResponsiveHeaderRenderer", out var header) ||
            first.TryGetPath(out header,
                "musicEditablePlaylistDetailHeaderRenderer", "header", "musicResponsiveHeaderRenderer"))
        {
            return header;
        }
        return null;
    }

    /// <summary>The account owns the playlist and may edit it: YTM sends the
    /// editable header only then (ytmusicapi's "owned").</summary>
    private static bool IsEditablePlaylist(JsonElement root) =>
        root.TryGetPath(out _,
            "contents", "twoColumnBrowseResultsRenderer", "tabs", "0",
            "tabRenderer", "content", "sectionListRenderer", "contents", "0",
            "musicEditablePlaylistDetailHeaderRenderer");

    /// <summary>Appends the shelf's tracks; returns an inline continuation token if present.</summary>
    private static ContinuationToken? AppendShelfTracks(JsonElement shelf, List<TrackInfo> tracks)
    {
        return shelf.TryGetProperty("contents", out var items)
            ? AppendItems(items, tracks)
            : null;
    }

    private static ContinuationToken? AppendItems(JsonElement items, List<TrackInfo> tracks)
    {
        ContinuationToken? continuation = null;

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("musicResponsiveListItemRenderer", out var renderer))
            {
                if (ParseHelpers.TryParseListItemTrack(renderer, out var track))
                    tracks.Add(track);
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
