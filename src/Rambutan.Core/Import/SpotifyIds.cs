namespace Rambutan.Core.Import;

/// <summary>Spotify's track ids, as its files write them.</summary>
public static class SpotifyIds
{
    private const int Length = 22;

    /// <summary>
    /// The 22-character id in <c>spotify:track:&lt;id&gt;</c>, in an
    /// <c>open.spotify.com/track/&lt;id&gt;</c> link, or on its own (TuneMyMusic's
    /// "Spotify - id"); null for anything else, a local file's
    /// <c>spotify:local:</c> URI included.
    /// </summary>
    public static string? TrackId(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        const string uri = "spotify:track:";
        if (text.StartsWith(uri, StringComparison.Ordinal))
            text = text[uri.Length..];
        else if (Uri.TryCreate(text, UriKind.Absolute, out var link))
        {
            if (!link.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase))
                return null;
            // "/track/<id>", or "/intl-de/track/<id>" for a localized link.
            var segments = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var track = Array.IndexOf(segments, "track");
            if (track < 0 || track + 1 >= segments.Length)
                return null;
            text = segments[track + 1];
        }

        return IsId(text) ? text : null;
    }

    internal static bool IsId(string text) =>
        text.Length == Length && text.All(char.IsAsciiLetterOrDigit);
}
