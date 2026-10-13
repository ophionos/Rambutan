using System.Text.Json;
using Rambutan.Core.Models;

namespace Rambutan.InnerTube.Parsing;

/// <summary>Parses `next` (watch-queue) responses into a radio track list.</summary>
public static class NextResponseParser
{
    public static IReadOnlyList<TrackInfo> ParseWatchQueue(JsonElement root)
    {
        if (!root.TryGetPath(out var contents,
                "contents", "singleColumnMusicWatchNextResultsRenderer", "tabbedRenderer",
                "watchNextTabbedResultsRenderer", "tabs", "0", "tabRenderer", "content",
                "musicQueueRenderer", "content", "playlistPanelRenderer", "contents"))
        {
            return [];
        }

        // The player overlay rates the seed track only; the panel rows carry none.
        var tracks = new List<TrackInfo>();
        foreach (var item in contents.EnumerateArray())
        {
            // Automix rows arrive wrapped (counterpart renderer alongside); the
            // primary renderer is the playable row either way.
            JsonElement renderer;
            if (item.TryGetProperty("playlistPanelVideoRenderer", out var direct))
                renderer = direct;
            else if (item.TryGetPath(out var wrapped,
                "playlistPanelVideoWrapperRenderer", "primaryRenderer",
                "playlistPanelVideoRenderer"))
                renderer = wrapped;
            else
                continue;

            if (TryParsePanelVideo(renderer, out var track))
            {
                tracks.Add(ParseRating(root, track.VideoId) is { } rating
                    ? track with { Rating = rating }
                    : track);
            }
        }

        return tracks;
    }

    /// <summary>
    /// The user's rating of <paramref name="videoId"/> from the player overlay's
    /// like button (signed-in responses reflect the account; anonymous ones always
    /// say INDIFFERENT). Null when the overlay is absent or rates another video.
    /// </summary>
    public static Rating? ParseRating(JsonElement root, string videoId)
    {
        if (!root.TryGetPath(out var actions, "playerOverlays", "playerOverlayRenderer", "actions") ||
            actions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var action in actions.EnumerateArray())
        {
            if (action.TryGetProperty("likeButtonRenderer", out var like) &&
                like.TryGetPath(out var target, "target", "videoId") &&
                target.ValueKind == JsonValueKind.String &&
                target.GetString() == videoId &&
                like.TryGetProperty("likeStatus", out var status))
            {
                return ParseHelpers.ParseLikeStatus(status);
            }
        }

        return null;
    }

    private static bool TryParsePanelVideo(JsonElement renderer, out TrackInfo track)
    {
        try
        {
            return TryParsePanelVideoCore(renderer, out track);
        }
        catch (Exception e) when (ParseHelpers.IsMalformedItem(e))
        {
            track = null!;
            return false;
        }
    }

    private static bool TryParsePanelVideoCore(JsonElement renderer, out TrackInfo track)
    {
        track = null!;

        var videoId = renderer.TryGetProperty("videoId", out var vid) ? vid.GetString() : null;
        var title = ParseHelpers.RunsText(renderer, "title");
        if (string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(title))
            return false;

        // The byline mixes artists, album, and view-count runs; pick by page type
        // like the list-item parser (separator runs carry no endpoint).
        var artists = new List<ArtistRef>();
        string? album = null;
        string? albumBrowseId = null;
        if (renderer.TryGetPath(out var bylineRuns, "longBylineText", "runs"))
        {
            foreach (var run in bylineRuns.EnumerateArray())
            {
                var text = run.TryGetProperty("text", out var rt) ? rt.GetString() : null;
                if (string.IsNullOrEmpty(text))
                    continue;

                switch (ParseHelpers.PageType(run))
                {
                    case "MUSIC_PAGE_TYPE_ARTIST":
                    case "MUSIC_PAGE_TYPE_USER_CHANNEL":
                        artists.Add(new ArtistRef(text, ParseHelpers.BrowseId(run)));
                        break;
                    case "MUSIC_PAGE_TYPE_ALBUM":
                        album = text;
                        albumBrowseId = ParseHelpers.BrowseId(run);
                        break;
                }
            }
        }

        TimeSpan? duration = null;
        if (ParseHelpers.RunsText(renderer, "lengthText") is { } length &&
            ParseHelpers.TryParseDuration(length, out var parsed))
        {
            duration = parsed;
        }

        // Panel rows carry a bare thumbnail object, not a musicThumbnailRenderer.
        string? thumbnailUrl = null;
        if (renderer.TryGetPath(out var thumbs, "thumbnail", "thumbnails") &&
            thumbs.ValueKind == JsonValueKind.Array && thumbs.GetArrayLength() > 0 &&
            thumbs[thumbs.GetArrayLength() - 1].TryGetProperty("url", out var url) &&
            url.ValueKind == JsonValueKind.String)
        {
            thumbnailUrl = url.GetString();
        }

        track = new TrackInfo
        {
            VideoId = videoId,
            Title = title,
            Artists = artists,
            Album = album,
            AlbumBrowseId = albumBrowseId,
            Duration = duration,
            ThumbnailUrl = thumbnailUrl,
            SongId = ParseHelpers.CreditsSongId(renderer),
        };
        return true;
    }
}
