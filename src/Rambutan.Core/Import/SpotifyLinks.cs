namespace Rambutan.Core.Import;

public enum SpotifyListKind
{
    Playlist,
    Album,
}

/// <summary>A public Spotify playlist or album, as its share link names it.</summary>
public sealed record SpotifyListLink(SpotifyListKind Kind, string Id)
{
    private string KindName => Kind == SpotifyListKind.Album ? "album" : "playlist";

    /// <summary><c>spotify:playlist:&lt;id&gt;</c>: what an import records as its source.</summary>
    public string Uri => $"spotify:{KindName}:{Id}";

    public string WebUrl => $"https://open.spotify.com/{KindName}/{Id}";

    /// <summary>The page Spotify's embedded player loads, which lists the songs.</summary>
    public string EmbedUrl => $"https://open.spotify.com/embed/{KindName}/{Id}";
}

/// <summary>Spotify's share links for playlists and albums.</summary>
public static class SpotifyLinks
{
    /// <summary>
    /// The playlist or album in an <c>open.spotify.com</c> link (with or
    /// without a <c>/intl-xx</c> segment, <c>/embed</c> or a query) or a
    /// <c>spotify:playlist:</c> / <c>spotify:album:</c> URI; null for
    /// anything else, a short link included (<see cref="IsShortLink"/>).
    /// </summary>
    public static SpotifyListLink? Parse(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        string[] segments;
        if (text.StartsWith("spotify:", StringComparison.Ordinal))
            segments = text.Split(':');
        else if (System.Uri.TryCreate(text, UriKind.Absolute, out var link) &&
                 link.Scheme is "https" or "http" &&
                 link.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase))
            segments = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        else
            return null;

        for (var i = 0; i + 1 < segments.Length; i++)
        {
            SpotifyListKind? kind = segments[i] switch
            {
                "playlist" => SpotifyListKind.Playlist,
                "album" => SpotifyListKind.Album,
                _ => null,
            };
            if (kind is not null)
                return SpotifyIds.IsId(segments[i + 1]) ? new SpotifyListLink(kind.Value, segments[i + 1]) : null;
        }
        return null;
    }

    /// <summary>
    /// A <c>spotify.link</c>, <c>spotify.app.link</c> or <c>spoti.fi</c> link, which names its list
    /// only by redirecting to another host: the import doesn't follow it, so
    /// the user is asked for the address it opens.
    /// </summary>
    public static bool IsShortLink(string? value) =>
        System.Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var link) &&
        link.Host.ToLowerInvariant() is "spotify.link" or "spotify.app.link" or "spoti.fi";

    /// <summary>The words of a pasted text, where each link is one: a list of
    /// links separated by spaces, commas or lines.</summary>
    public static IReadOnlyList<string> Words(string? text) =>
        (text ?? "").Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
}
