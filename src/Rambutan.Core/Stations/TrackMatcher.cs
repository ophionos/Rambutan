using System.Globalization;
using System.Text;
using Rambutan.Core.Models;

namespace Rambutan.Core.Stations;

/// <summary>
/// Picks the YouTube Music song that is the same recording as a station track,
/// from the results of searching "artist title". Measured on 289 real station
/// tracks: names decide, and duration only ranks, because the stations'
/// durations are often wrong or missing (46 s for a 5-minute song).
/// </summary>
public static class TrackMatcher
{
    /// <summary>
    /// Bump when a rule change could change a pick: saved search outcomes
    /// (<see cref="StationMatchCache"/>) made under another version are dropped
    /// rather than standing in for what the new rules would pick.
    /// </summary>
    public const int RulesVersion = 1;

    /// <summary>A song whose duration is this close counts as the same length.</summary>
    public static readonly TimeSpan SameLength = TimeSpan.FromSeconds(10);

    /// <summary>Close enough to accept a title match whose artist is named
    /// differently (an alias: "Cansei de Ser Sexy" is "CSS" on YouTube Music).</summary>
    public static readonly TimeSpan AliasLength = TimeSpan.FromSeconds(2);

    // Words in a title's brackets or dash suffix that make it another recording.
    // Remasters, radio edits, "Main Version" and the like are the same song.
    private static readonly HashSet<string> VersionWords = new(StringComparer.Ordinal)
    {
        "remix", "remixed", "rmx", "live", "acoustic", "instrumental", "demo", "karaoke", "cover",
        "sped", "slowed", "nightcore", "extended", "dub", "unplugged", "reprise",
    };

    private enum Tier { Same = 0, OtherLength = 1, Alias = 2, OtherVersion = 3, None = 4 }

    /// <summary>
    /// The best candidate, or null when none is the same recording. A candidate
    /// with a version word on one side only ("Remix", "Live") is taken only
    /// when <paramref name="lastResort"/> is set, since the station's own link is
    /// the better guess while there is one.
    /// </summary>
    public static TrackInfo? Pick(StationTrack track, IReadOnlyList<TrackInfo> candidates, bool lastResort = false)
    {
        var title = TitleParts.Of(track.Title);
        var artist = NormalizeArtist(track.Artist);

        TrackInfo? best = null;
        var bestTier = Tier.None;
        var bestDiff = TimeSpan.MaxValue;
        foreach (var candidate in candidates)
        {
            var tier = Rank(track, title, artist, candidate);
            if (tier == Tier.None || (tier == Tier.OtherVersion && !lastResort))
                continue;
            var diff = track.Duration is { } a && candidate.Duration is { } b ? (a - b).Duration() : SameLength;
            if (tier < bestTier || (tier == bestTier && diff < bestDiff))
            {
                best = candidate;
                bestTier = tier;
                bestDiff = diff;
            }
        }
        return best;
    }

    private static Tier Rank(StationTrack track, TitleParts title, string[] artist, TrackInfo candidate)
    {
        var other = TitleParts.Of(candidate.Title);
        var exactTitle = title.Base == other.Base || Squash(title.Base) == Squash(other.Base);
        var artistOk = candidate.Artists.Any(a => ArtistsMatch(artist, NormalizeArtist(a.Name)));
        // A one-letter slip ("Ecstacy", "Gypsy") counts only for a long enough
        // title by the same artist.
        var titleOk = exactTitle || (artistOk && title.Base.Length >= 8 && WithinOneEdit(title.Base, other.Base));
        if (!titleOk)
            return Tier.None;

        var known = track.Duration is { } a && candidate.Duration is { } b ? (a - b).Duration() : (TimeSpan?)null;
        if (!artistOk)
            return exactTitle && title.Versions.SetEquals(other.Versions) && known <= AliasLength ? Tier.Alias : Tier.None;
        if (!title.Versions.SetEquals(other.Versions))
            return Tier.OtherVersion;
        return known is null || known <= SameLength ? Tier.Same : Tier.OtherLength;
    }

    /// <summary>One artist's name is the other's, or begins it word for word:
    /// "Joan Jett and the Blackhearts" is "Joan Jett".</summary>
    private static bool ArtistsMatch(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0)
            return false;
        if (string.Concat(a) == string.Concat(b))
            return true;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.AsSpan().SequenceEqual(longer.AsSpan(0, shorter.Length));
    }

    private static string[] NormalizeArtist(string name)
    {
        var words = Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words is ["the", .. var rest] && rest.Length > 0 ? rest : words;
    }

    private static string Squash(string s) => s.Replace(" ", "", StringComparison.Ordinal);

    /// <summary>Lower case, no accents, apostrophes dropped ("can’t" is "cant"),
    /// "&amp;" as "and", every other mark a single space.</summary>
    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = true;
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '’' or '‘' or '`')
                continue;
            if (c == '&')
            {
                if (!space)
                    sb.Append(' ');
                sb.Append("and ");
                space = true;
            }
            else if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().Trim();
    }

    private static bool WithinOneEdit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
            return false;
        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++edits > 1)
                return false;
            if (a.Length > b.Length) i++;
            else if (b.Length > a.Length) j++;
            else { i++; j++; }
        }
        return edits + (a.Length - i) + (b.Length - j) <= 1;
    }

    /// <summary>A title without its qualifiers, and the version words among them.
    /// Qualifiers are bracketed parts, a " - " suffix ("- Remastered 2011") and
    /// a featured-artist credit.</summary>
    private sealed record TitleParts(string Base, HashSet<string> Versions)
    {
        public static TitleParts Of(string title)
        {
            var qualifiers = new StringBuilder();
            var main = new StringBuilder();
            var depth = 0;
            foreach (var c in title)
            {
                if (c is '(' or '[') { depth++; qualifiers.Append(' '); continue; }
                if (c is ')' or ']' && depth > 0) { depth--; qualifiers.Append(' '); continue; }
                (depth > 0 ? qualifiers : main).Append(c);
            }

            var head = main.ToString();
            foreach (var dash in (ReadOnlySpan<string>)[" - ", " – ", " — "])
            {
                var at = head.IndexOf(dash, StringComparison.Ordinal);
                if (at > 0)
                {
                    qualifiers.Append(' ').Append(head[(at + dash.Length)..]);
                    head = head[..at];
                }
            }

            var words = Normalize(head).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var feat = words.FindIndex(w => w is "feat" or "ft" or "featuring");
            if (feat > 0)
                words.RemoveRange(feat, words.Count - feat);

            var versions = Normalize(qualifiers.ToString())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(VersionWords.Contains)
                .Select(w => w is "rmx" or "remixed" ? "remix" : w)
                .ToHashSet(StringComparer.Ordinal);
            return new TitleParts(string.Join(' ', words), versions);
        }
    }
}
