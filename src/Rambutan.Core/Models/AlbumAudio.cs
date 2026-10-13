namespace Rambutan.Core.Models;

/// <summary>
/// Which id an album page's row plays. Signed out, YouTube Music lists an
/// album's music videos (OMV), whose intros, edits or mixes often differ from the
/// album; the row's <see cref="TrackInfo.SongId"/> names the album audio track.
/// Album pages play that, and keep the listed video as the fallback for when the
/// audio track won't resolve. Every other surface plays the video it lists: there
/// the user picked that video.
/// </summary>
public static class AlbumAudio
{
    /// <summary>
    /// The row as an album page plays it: <see cref="TrackInfo.VideoId"/> becomes
    /// the SongId and <see cref="TrackInfo.ListedVideoId"/> keeps the video the page
    /// listed. A row with no SongId, or already the audio track, is returned as is.
    /// So is a row whose listed video is downloaded and whose audio track isn't: a
    /// download made before album pages played the audio stays what plays, and is
    /// not fetched again.
    /// </summary>
    /// <param name="hasDownload">Whether a videoId has a download that plays.</param>
    public static TrackInfo Prefer(TrackInfo row, Func<string, bool>? hasDownload = null)
    {
        if (string.IsNullOrEmpty(row.SongId) || row.SongId == row.VideoId)
            return row;
        if (hasDownload is not null && hasDownload(row.VideoId) && !hasDownload(row.SongId))
            return row;
        return row with { VideoId = row.SongId, ListedVideoId = row.VideoId };
    }

    /// <summary>
    /// The track to play once its album audio track wouldn't resolve: the video the
    /// page listed, with no further fallback. Null when there is nothing to fall
    /// back to.
    /// </summary>
    public static TrackInfo? Fallback(TrackInfo track) =>
        string.IsNullOrEmpty(track.ListedVideoId) || track.ListedVideoId == track.VideoId
            ? null
            : track with { VideoId = track.ListedVideoId, ListedVideoId = null };
}
