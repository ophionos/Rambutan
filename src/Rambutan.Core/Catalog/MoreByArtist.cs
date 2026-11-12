using Rambutan.Core.Models;

namespace Rambutan.Core.Catalog;

/// <summary>
/// What an album page shows of its artist beside the tracks: the artist's top
/// songs, then the Albums and Singles &amp; EPs shelves, then "Fans might also
/// like", in that order whatever the artist page's own. The artist page's other
/// shelves (videos, playlists, "Featured on") are left out.
/// </summary>
public sealed record MoreByArtist(IReadOnlyList<TrackInfo> TopSongs, IReadOnlyList<Shelf> Shelves)
{
    /// <summary>The shelves taken, by the titles YouTube Music gives them.
    /// InnerTube always asks in English, so the titles are these.</summary>
    private static readonly string[] Titles = ["Albums", "Singles & EPs", "Fans might also like"];

    public bool IsEmpty => TopSongs.Count == 0 && Shelves.Count == 0;

    /// <param name="albumBrowseId">The album being shown, which the Albums and
    /// Singles &amp; EPs shelves leave out. A shelf left with no card is dropped.</param>
    public static MoreByArtist From(ArtistPage artist, string albumBrowseId)
    {
        var shelves = new List<Shelf>();
        foreach (var title in Titles)
        {
            if (artist.Shelves.FirstOrDefault(s => s.Title == title) is not { } shelf)
                continue;
            var entries = shelf.Entries
                .Where(e => e is not AlbumEntry album || album.BrowseId != albumBrowseId)
                .ToList();
            if (entries.Count > 0)
                shelves.Add(entries.Count == shelf.Entries.Count ? shelf : shelf with { Entries = entries });
        }
        return new MoreByArtist(artist.TopSongs, shelves);
    }
}
