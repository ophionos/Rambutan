namespace Rambutan.Core.Models;

/// <summary>
/// Whether an album page's row can leave out its artist line: the header names
/// the album's artist once, so a row that only repeats it says nothing new.
/// A row with anyone else (a feature, another artist) keeps the line.
/// </summary>
public static class AlbumArtistLine
{
    /// <summary>
    /// True when <paramref name="rowArtists"/> are just <paramref name="albumArtist"/>:
    /// one credit with the album artist's browse id, or credits whose names,
    /// joined as the row shows them, are the album artist's name, trimmed and
    /// ignoring case (one credit of that name, or an album credited to "A, B"
    /// whose rows are A and B). False without an album artist.
    /// </summary>
    public static bool Hides(ArtistRef? albumArtist, IReadOnlyList<ArtistRef>? rowArtists)
    {
        if (albumArtist is null || string.IsNullOrWhiteSpace(albumArtist.Name) || rowArtists is not { Count: > 0 })
            return false;

        if (rowArtists.Count == 1
            && !string.IsNullOrEmpty(rowArtists[0].BrowseId) && !string.IsNullOrEmpty(albumArtist.BrowseId)
            && string.Equals(rowArtists[0].BrowseId, albumArtist.BrowseId, StringComparison.Ordinal))
        {
            return true;
        }

        // By name too when the ids differ: YouTube Music has more than one channel
        // for many artists, and the line would still read as a repeat.
        return SameName(string.Join(", ", rowArtists.Select(a => a.Name.Trim())), albumArtist.Name);
    }

    private static bool SameName(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
