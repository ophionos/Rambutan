using Rambutan.Core.Local;
using Rambutan.Core.Models;

namespace Rambutan.Core.Playlists;

/// <summary>
/// A playlist of the user's own, kept on this computer: YouTube Music songs and
/// local files together. It never writes to YouTube Music, and a copy of a
/// YouTube Music playlist (<see cref="CopiedFrom"/>) diverges from it at once.
/// </summary>
/// <param name="CopiedFrom">The YouTube Music playlist it was copied from; null
/// for one the user started.</param>
public sealed record LocalPlaylist(
    string Id, string Name, IReadOnlyList<LocalPlaylistItem> Items,
    DateTimeOffset CreatedAt, DateTimeOffset ChangedAt, string? CopiedFrom = null);

/// <summary>
/// One row of a <see cref="LocalPlaylist"/>. A YouTube Music song is its track
/// as it was added, so the row shows offline. A local file is its
/// <c>local:&lt;n&gt;</c> id with the <see cref="Path"/> and the tags
/// (<see cref="Track"/>) it had then: when the file moves, it gets a new id, and
/// the path or the tags find it again (<see cref="LocalPlaylists.Resolve"/>).
/// </summary>
/// <param name="Path">The local file's path; null for a YouTube Music song.</param>
public sealed record LocalPlaylistItem(TrackInfo Track, string? Path = null)
{
    public bool IsLocal => LocalIds.IsLocal(Track.VideoId);

    public static LocalPlaylistItem Of(TrackInfo track) => new(track);

    public static LocalPlaylistItem Of(LocalFile file) => new(LocalTracks.ToTrack(file), file.Path);
}

/// <summary>A row as it shows now.</summary>
/// <param name="Item">The row, with a found file's current id, path and tags.</param>
/// <param name="IsMissing">A local file that is gone and wasn't found again.</param>
public sealed record ResolvedPlaylistItem(LocalPlaylistItem Item, bool IsMissing);
