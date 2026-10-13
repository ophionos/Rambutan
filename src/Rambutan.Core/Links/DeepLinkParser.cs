namespace Rambutan.Core.Links;

/// <summary>
/// Parses <c>rambutan://</c> links and music.youtube.com URLs. Pure, and
/// strict on purpose: a link can come from any web page, so ids must have their
/// known shape and nothing is decoded twice.
/// <para>
/// Grammar, version 1. The host names the action; an id is one path segment.
/// <code>
/// rambutan://                       rambutan://open
/// rambutan://play/&lt;videoId&gt;        11 characters
/// rambutan://album/&lt;browseId&gt;      MPRE…
/// rambutan://playlist/&lt;id&gt;         PL…, OLAK5uy_…, RD…; a VL prefix is dropped
/// rambutan://artist/&lt;channelId&gt;    UC + 22 characters
/// rambutan://search?q=&lt;text&gt;       percent- or plus-encoded
/// </code>
/// An action this version doesn't know is refused as unknown, never guessed at,
/// so a later version can add actions without older ones misreading them.
/// Query parameters an action doesn't use are ignored, for the same reason.
/// </para>
/// </summary>
public static class DeepLinkParser
{
    public const string Scheme = "rambutan";

    public const int MaxLinkLength = 2048;

    /// <summary>Longest search text accepted, after decoding.</summary>
    public const int MaxQueryLength = 200;

    private const int MaxActionLength = 32;
    private const int MaxAlbumIdLength = 64;
    private const int MaxPlaylistIdLength = 80;
    private const string YouTubeMusicHost = "music.youtube.com";

    /// <summary>Whether <paramref name="text"/> claims to be a link this parser
    /// handles, valid or not: what the command line passes to <see cref="Parse"/>.</summary>
    public static bool LooksLikeLink(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        var trimmed = text.AsSpan().Trim();
        return trimmed.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)
               || TryStripPrefix(trimmed, "https://" + YouTubeMusicHost, out _)
               || TryStripPrefix(trimmed, "http://" + YouTubeMusicHost, out _);
    }

    public static DeepLinkResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return DeepLinkResult.Malformed();
        var link = text.Trim();
        if (link.Length > MaxLinkLength || HasControlOrSpace(link))
            return DeepLinkResult.Malformed();

        if (TryStripPrefix(link, Scheme + "://", out var rest))
            return ParseRambutan(rest);
        if (TryStripPrefix(link, "https://", out rest) || TryStripPrefix(link, "http://", out rest))
            return ParseWeb(rest);
        return DeepLinkResult.Malformed();
    }

    private static DeepLinkResult ParseRambutan(string rest)
    {
        var (path, query) = SplitQuery(rest);
        if (!TrySegments(path, out var segments))
            return DeepLinkResult.Malformed();

        if (segments.Length == 0)
            return DeepLinkResult.Ok(new OpenAppLink());

        var action = segments[0].ToLowerInvariant();
        var ids = segments.AsSpan(1);
        switch (action)
        {
            case "open" when ids.IsEmpty:
                return DeepLinkResult.Ok(new OpenAppLink());
            case "play" when ids.Length == 1:
                return Video(ids[0]);
            case "album" when ids.Length == 1:
                return Album(ids[0]);
            case "playlist" when ids.Length == 1:
                return Playlist(ids[0]);
            case "artist" when ids.Length == 1:
                return Artist(ids[0]);
            case "search" when ids.IsEmpty:
                return Search(Param(query, "q"));
            case "open" or "play" or "album" or "playlist" or "artist" or "search":
                return DeepLinkResult.Malformed();
            default:
                return IsAction(action) ? DeepLinkResult.Unknown(action) : DeepLinkResult.Malformed();
        }
    }

    /// <summary>music.youtube.com URLs, the forms its Share button and address bar give.</summary>
    private static DeepLinkResult ParseWeb(string rest)
    {
        var (beforeQuery, query) = SplitQuery(rest);
        var slash = beforeQuery.IndexOf('/');
        var host = slash < 0 ? beforeQuery : beforeQuery[..slash];
        var path = slash < 0 ? "" : beforeQuery[(slash + 1)..];
        if (!host.Equals(YouTubeMusicHost, StringComparison.OrdinalIgnoreCase))
            return DeepLinkResult.Malformed();
        if (!TrySegments(path, out var segments))
            return DeepLinkResult.Malformed();

        if (segments.Length == 0)
            return DeepLinkResult.Ok(new OpenAppLink());

        var first = segments[0];
        return (first, segments.Length) switch
        {
            ("watch", 1) => Video(Param(query, "v")),
            ("playlist", 1) => Playlist(Param(query, "list")),
            ("search", 1) => Search(Param(query, "q")),
            ("channel", 2) => Artist(segments[1]),
            ("browse", 2) when segments[1].StartsWith("MPRE", StringComparison.Ordinal) => Album(segments[1]),
            ("browse", 2) when segments[1].StartsWith("VL", StringComparison.Ordinal) => Playlist(segments[1]),
            ("browse", 2) when segments[1].StartsWith("UC", StringComparison.Ordinal) => Artist(segments[1]),
            _ => IsAction(first.ToLowerInvariant())
                ? DeepLinkResult.Unknown(first.ToLowerInvariant())
                : DeepLinkResult.Malformed(),
        };
    }

    private static DeepLinkResult Video(string? id) =>
        id is { Length: 11 } && IsId(id)
            ? DeepLinkResult.Ok(new PlayLink(id))
            : DeepLinkResult.Malformed();

    private static DeepLinkResult Album(string id) =>
        id.Length is > 4 and <= MaxAlbumIdLength && id.StartsWith("MPRE", StringComparison.Ordinal) && IsId(id)
            ? DeepLinkResult.Ok(new AlbumLink(id))
            : DeepLinkResult.Malformed();

    private static DeepLinkResult Artist(string id) =>
        id.Length == 24 && id.StartsWith("UC", StringComparison.Ordinal) && IsId(id)
            ? DeepLinkResult.Ok(new ArtistLink(id))
            : DeepLinkResult.Malformed();

    private static DeepLinkResult Playlist(string? id)
    {
        if (id is null)
            return DeepLinkResult.Malformed();
        // The pages and the catalog take the bare id; YTM's browse form adds VL.
        if (id.StartsWith("VL", StringComparison.Ordinal))
            id = id[2..];
        return id.Length is >= 2 and <= MaxPlaylistIdLength && IsId(id)
            ? DeepLinkResult.Ok(new PlaylistLink(id))
            : DeepLinkResult.Malformed();
    }

    private static DeepLinkResult Search(string? raw)
    {
        if (raw is null)
            return DeepLinkResult.Malformed();
        // Decoded once, here; '+' is a space in a query string.
        var text = Uri.UnescapeDataString(raw.Replace('+', ' ')).Trim();
        if (text.Length is 0 or > MaxQueryLength || text.Any(char.IsControl))
            return DeepLinkResult.Malformed();
        return DeepLinkResult.Ok(new SearchLink(text));
    }

    private static (string Path, string Query) SplitQuery(string rest)
    {
        var hash = rest.IndexOf('#');
        if (hash >= 0)
            rest = rest[..hash];
        var q = rest.IndexOf('?');
        return q < 0 ? (rest, "") : (rest[..q], rest[(q + 1)..]);
    }

    /// <summary>Path segments. One trailing slash is tolerated (some browsers and
    /// Windows add it); an empty segment anywhere else is malformed.</summary>
    private static bool TrySegments(string path, out string[] segments)
    {
        if (path.EndsWith('/'))
            path = path[..^1];
        segments = path.Length == 0 ? [] : path.Split('/');
        return segments.All(s => s.Length > 0);
    }

    /// <summary>The first value of <paramref name="name"/>, still encoded.</summary>
    private static string? Param(string query, string name)
    {
        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair.AsSpan(0, eq).SequenceEqual(name))
                return pair[(eq + 1)..];
        }
        return null;
    }

    /// <summary>YouTube's id alphabet: letters, digits, '-' and '_'. No dots or
    /// slashes, so no id can name a path, and no '%', so nothing decodes later.</summary>
    private static bool IsId(string id) =>
        id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');

    private static bool IsAction(string action) =>
        action.Length is > 0 and <= MaxActionLength
        && action.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    private static bool HasControlOrSpace(string text) =>
        text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));

    private static bool TryStripPrefix(ReadOnlySpan<char> text, string prefix, out string rest)
    {
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = text[prefix.Length..].ToString();
            return true;
        }
        rest = "";
        return false;
    }
}
