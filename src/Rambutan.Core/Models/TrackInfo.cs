namespace Rambutan.Core.Models;

/// <summary>
/// An artist credit on a track. BrowseId is null when the source row carried no
/// artist link (album-header fallbacks, radio bylines without endpoints).
/// </summary>
public sealed record ArtistRef(string Name, string? BrowseId = null);

/// <summary>
/// A song's place in the library as its source row reported it, with the two
/// feedback tokens YTM's "Save to library" toggle sends. The tokens belong to the
/// row's menu; only a signed-in response carries them.
/// </summary>
public sealed record LibraryToggle(bool InLibrary, string AddToken, string RemoveToken);

public sealed record TrackInfo
{
    public required string VideoId { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<ArtistRef> Artists { get; init; }
    public string? Album { get; init; }

    /// <summary>The album's browse id (MPREb_…), where the source row links it.
    /// Null for singles without an album link and for rows that name no album.</summary>
    public string? AlbumBrowseId { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? ThumbnailUrl { get; init; }

    /// <summary>
    /// Position on its album as the source numbers it ("1", "2", …). Set only on
    /// album pages; a playlist's or a search's order isn't a track number.
    /// </summary>
    public int? TrackNumber { get; init; }

    /// <summary>
    /// Identity of this row within its source playlist (playlistSetVideoId);
    /// required to remove the row. Null everywhere except playlist pages.
    /// </summary>
    public string? SetVideoId { get; init; }

    /// <summary>
    /// The user's rating as the source response reported it. Null when that
    /// surface doesn't carry one (search rows, radio rows other than the seed),
    /// which is "unknown", not "not rated". A snapshot from load time: the live
    /// value, including this session's changes, is <see cref="Catalog.TrackRatings"/>.
    /// </summary>
    public Rating? Rating { get; init; }

    /// <summary>
    /// Whether the song is in the library, and how to change that. Null when the
    /// source row didn't say (signed out, radio rows), which is "unknown", not
    /// "not in the library". A snapshot from load time, like <see cref="Rating"/>:
    /// the live value is <see cref="Catalog.SongLibrary"/>.
    /// </summary>
    public LibraryToggle? Library { get; init; }

    /// <summary>
    /// The audio track YouTube Music names as this row's song: the target of the
    /// row's "View song credits" item (MPTC…). Equal to <see cref="VideoId"/> on an
    /// audio-track row; the audio-track counterpart on an album page's music-video
    /// row. Null when the row has no credits item (music videos outside album
    /// pages, user uploads), which is "counterpart unknown". See <see cref="TrackIdentity"/>.
    /// </summary>
    public string? SongId { get; init; }

    /// <summary>
    /// The video an album page listed for this row, when the row plays its
    /// <see cref="SongId"/> instead (<see cref="AlbumAudio"/>); playback falls back
    /// to it when the audio track won't resolve. Null everywhere else.
    /// </summary>
    public string? ListedVideoId { get; init; }

    public TrackIdentity Identity => new(VideoId, SongId);

    public string ArtistDisplay => string.Join(", ", Artists.Select(a => a.Name));

    /// <summary>"Go to artist" target: the first credit that carries a link.</summary>
    public ArtistRef? PrimaryArtist => Artists.FirstOrDefault(a => a.BrowseId is not null);
}
