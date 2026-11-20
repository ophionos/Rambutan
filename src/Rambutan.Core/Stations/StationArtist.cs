namespace Rambutan.Core.Stations;

/// <summary>Whether a service's artist credit is for the artist a station was asked for.</summary>
public static class StationArtist
{
    private static readonly string[] Separators =
        [" & ", ", ", " feat", " ft", " x ", " + ", " and ", " / "];

    /// <summary>
    /// True when <paramref name="credit"/> is <paramref name="artist"/> alone or
    /// starts with it followed by a credit separator ("Fausto &amp; Sérgio
    /// Godinho"), ignoring case and accents. A credit that only begins with the
    /// same letters ("Faustones") or has the artist later in it is not.
    /// </summary>
    public static bool Names(string credit, string artist)
    {
        var name = TrackMatcher.Normalize(artist);
        if (name.Length == 0 || TrackMatcher.Normalize(credit).Length == 0)
            return false;
        if (TrackMatcher.Normalize(credit) == name)
            return true;

        // The name may hold a separator itself ("Simon & Garfunkel"), so the cut
        // is tried at each one rather than the first.
        foreach (var separator in Separators)
        {
            for (var at = credit.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
                 at > 0;
                 at = credit.IndexOf(separator, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                if (TrackMatcher.Normalize(credit[..at]) == name)
                    return true;
            }
        }
        return false;
    }
}
