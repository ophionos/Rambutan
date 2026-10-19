using Rambutan.Core.Models;

namespace Rambutan.Core.Links;

/// <summary>
/// The music.youtube.com links Share copies: the forms YouTube Music's own Share
/// button gives, so they open there and, through <see cref="DeepLinkParser"/>, in
/// Rambutan.
/// </summary>
public static class ShareLinks
{
    private const string Base = "https://music.youtube.com/";

    public static string ForTrack(TrackInfo track) => $"{Base}watch?v={Uri.EscapeDataString(track.VideoId)}";

    /// <summary>The link for an album, playlist or artist card; null for a song
    /// card (its track has <see cref="ForTrack"/>) and for an id whose link
    /// wouldn't open that page: one <see cref="DeepLinkParser"/> refuses, or reads
    /// as another kind of page.</summary>
    public static string? ForEntry(CatalogEntry entry)
    {
        var link = entry switch
        {
            AlbumEntry album => $"{Base}browse/{Uri.EscapeDataString(album.BrowseId)}",
            PlaylistEntry playlist => $"{Base}playlist?list={Uri.EscapeDataString(playlist.PlaylistId)}",
            // Library rows name an artist by its library page, MPLA + the channel id.
            ArtistEntry artist => $"{Base}channel/{Uri.EscapeDataString(
                artist.BrowseId.StartsWith("MPLA", StringComparison.Ordinal) ? artist.BrowseId[4..] : artist.BrowseId)}",
            _ => null,
        };
        if (link is null)
            return null;

        var opens = DeepLinkParser.Parse(link).Link;
        return (entry, opens) switch
        {
            (AlbumEntry, AlbumLink) or (PlaylistEntry, PlaylistLink) or (ArtistEntry, ArtistLink) => link,
            _ => null,
        };
    }
}
