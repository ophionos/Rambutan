using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.App.ViewModels;

/// <summary>
/// What a card plays without opening the page: an album's tracks, a
/// playlist's first page (as Play all on the playlist page would, before
/// scrolling loads more), or an artist's top songs.
/// </summary>
public static class EntryPlayback
{
    /// <summary>True for the cards that get a play button: albums and playlists.
    /// A song card already plays on click; an artist has no single thing to play.</summary>
    public static bool HasPlayButton(CatalogEntry? entry) => entry is AlbumEntry or PlaylistEntry;

    /// <summary>The tracks to queue for an album, playlist or artist card; empty for
    /// a song. An album plays its audio tracks, as its page does; an artist its top
    /// songs, as its page's "Play top songs" does.</summary>
    /// <param name="hasDownload">Whether a videoId has a download that plays.</param>
    public static async Task<IReadOnlyList<TrackInfo>> ResolveAsync(
        ICatalog catalog, CatalogEntry entry, Func<string, bool>? hasDownload = null, CancellationToken ct = default) =>
        entry switch
        {
            AlbumEntry album => (await catalog.GetAlbumAsync(album.BrowseId, ct)).Tracks
                .Select(t => AlbumAudio.Prefer(t, hasDownload)).ToList(),
            PlaylistEntry playlist => (await catalog.GetPlaylistAsync(playlist.PlaylistId, ct)).Tracks,
            ArtistEntry artist => (await catalog.GetArtistAsync(artist.BrowseId, ct)).TopSongs,
            _ => [],
        };

    /// <summary>A shuffled copy for "Shuffle" on album and playlist headers
    /// (Fisher–Yates; the source list is left as it is).</summary>
    public static List<T> Shuffled<T>(IEnumerable<T> items, Random random)
    {
        var list = new List<T>(items);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}
