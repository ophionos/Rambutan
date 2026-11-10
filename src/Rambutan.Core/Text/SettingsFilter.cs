using System.Globalization;

namespace Rambutan.Core.Text;

/// <summary>
/// Whether a setting matches the Settings page's filter. The query is split into
/// words on whitespace, and a setting matches when every word occurs somewhere
/// in its texts (its title, its help, its section's heading), ignoring case and
/// diacritics, so "ecran" finds "Écran". An empty query matches everything.
/// </summary>
public static class SettingsFilter
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    public static string[] Words(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static bool IsEmpty(string? query) => string.IsNullOrWhiteSpace(query);

    public static bool Matches(string? query, IReadOnlyList<string?> texts) => Matches(Words(query), texts);

    /// <summary>Whether every one of <paramref name="words"/> occurs in one of
    /// <paramref name="texts"/>; true when there are no words.</summary>
    public static bool Matches(IReadOnlyList<string> words, IReadOnlyList<string?> texts)
    {
        foreach (var word in words)
        {
            var found = false;
            foreach (var text in texts)
            {
                if (!string.IsNullOrEmpty(text) && Compare.IndexOf(text, word, Options) >= 0)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;
        }
        return true;
    }
}
