namespace Rambutan.Core.Models;

/// <summary>
/// Which song a track row plays, across the surfaces that reach it.
/// </summary>
/// <remarks>
/// YouTube Music gives one album track different videoIds per surface: an
/// anonymous album page lists the music video (OMV), while search, the album's
/// audio playlist and radio list the audio track (ATV). It pairs them itself:
/// the album row's "View song credits" item names the audio track, which is
/// <see cref="TrackInfo.SongId"/>. Elsewhere a music video names nothing, so
/// its counterpart is unknown.
///
/// <para>There is deliberately no single canonical key. The same music video has
/// a SongId on the album page and none from a video search, so a key of
/// "SongId, else VideoId" would split one video into two identities. Matching
/// is by overlap instead (<see cref="SameSong"/>); with no counterpart known on
/// either side it is exactly the videoId match. Title and artist are never
/// compared: that would match covers, live cuts and remixes.</para>
///
/// <para>The pairing is YouTube Music's, not a recording match: one album row
/// pairs its audio track with a "Drumless Edition" video. Good enough to say
/// "this is playing"; not good enough to share timed lyrics or a downloaded
/// file, which stay keyed by videoId.</para>
/// </remarks>
public readonly record struct TrackIdentity(string VideoId, string? SongId)
{
    /// <summary>
    /// True when both name the same song: the same video, the same known song,
    /// or one's song is the other's video. False when either VideoId is empty.
    /// </summary>
    public bool SameSong(TrackIdentity other)
    {
        if (string.IsNullOrEmpty(VideoId) || string.IsNullOrEmpty(other.VideoId))
            return false;

        return VideoId == other.VideoId
            || (!string.IsNullOrEmpty(SongId) && (SongId == other.SongId || SongId == other.VideoId))
            || (!string.IsNullOrEmpty(other.SongId) && other.SongId == VideoId);
    }
}
