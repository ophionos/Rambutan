using Rambutan.Core.Models;

namespace Rambutan.Core.Stats;

/// <summary>
/// One play in the play log, with its own copy of what the track was called:
/// a local file's <c>local:&lt;n&gt;</c> id can stop resolving (an opened file is
/// forgotten), and the stats must still name it. Never a file's path, and no
/// artwork URL for a track that isn't YouTube's.
/// </summary>
public sealed record PlayRecord
{
    /// <summary>Names the play: a later record with the same id replaces an
    /// earlier one, so a play is written again as it goes on.</summary>
    public required string Id { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Audio actually heard (<see cref="Playback.ListenMeter"/>).</summary>
    public required TimeSpan Listened { get; init; }

    public required string VideoId { get; init; }
    public string? SongId { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<ArtistRef> Artists { get; init; } = [];
    public string? Album { get; init; }
    public string? AlbumBrowseId { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? ThumbnailUrl { get; init; }

    /// <summary>The song the play counts for: the audio track YouTube Music names
    /// for it where known, so its video and its audio track count together.</summary>
    public string SongKey => SongId ?? VideoId;

    public static PlayRecord From(string id, TrackInfo track, DateTimeOffset startedAt, TimeSpan listened)
    {
        var onYouTube = YouTubeIds.IsVideoId(track.VideoId);
        return new PlayRecord
        {
            Id = id,
            StartedAt = startedAt,
            Listened = listened,
            VideoId = track.VideoId,
            SongId = onYouTube ? track.SongId : null,
            Title = track.Title,
            Artists = [.. track.Artists.Select(a => onYouTube ? a : a with { BrowseId = null })],
            Album = track.Album,
            AlbumBrowseId = onYouTube ? track.AlbumBrowseId : null,
            Duration = track.Duration,
            ThumbnailUrl = onYouTube ? track.ThumbnailUrl : null,
        };
    }
}

/// <summary>
/// Which plays count for what (the maintainer's rules). All listening counts as
/// time. A play counts in the top lists once 30 s or half the track was heard,
/// whichever comes first. A YouTube play longer than 20 minutes (a stream, a
/// podcast, an hour-long mix) counts as time only, never as a song, artist or
/// album; a local file counts as a song at any length. The parsers don't carry
/// YouTube's video type, so a very long audio track counts as time only too.
/// </summary>
public static class PlayRules
{
    public static readonly TimeSpan CountsAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LongVideo = TimeSpan.FromMinutes(20);

    public static bool CountsAsPlay(PlayRecord play) =>
        play.Listened > TimeSpan.Zero && play.Listened >= Threshold(play.Duration);

    /// <summary>A play with no known length (a live stream) is judged by what was heard.</summary>
    public static bool IsTimeOnly(PlayRecord play) =>
        YouTubeIds.IsVideoId(play.VideoId) && (play.Duration ?? play.Listened) > LongVideo;

    /// <summary>How much listening makes a play count: 30 s, or half a shorter track.</summary>
    public static TimeSpan Threshold(TimeSpan? duration) =>
        duration is { } d && d / 2 < CountsAfter ? d / 2 : CountsAfter;
}
