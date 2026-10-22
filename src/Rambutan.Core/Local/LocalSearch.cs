using System.Globalization;
using System.Text;

namespace Rambutan.Core.Local;

/// <summary>What a search found among the local files: the first ones, best
/// first, and how many matched in all.</summary>
public sealed record LocalMatches(IReadOnlyList<LocalFile> Files, int Total)
{
    public static LocalMatches None { get; } = new([], 0);
}

/// <summary>
/// Searching the local index. Every word of the query has to appear in the
/// file's title (else its file name), artist, album artist or album, ignoring
/// case and accents. Nothing about it leaves the machine.
/// </summary>
public static class LocalSearch
{
    /// <summary>
    /// The playable files that match, ranked: a title equal to the query, then a
    /// title that starts with it, then a title that holds every word, then the
    /// rest; within each rank, the library's order. <paramref name="limit"/>
    /// caps <see cref="LocalMatches.Files"/>, not <see cref="LocalMatches.Total"/>.
    /// </summary>
    public static LocalMatches Find(IEnumerable<LocalFile> files, string query, int limit, LocalPaths? paths = null)
    {
        paths ??= LocalPaths.Platform;
        var folded = Fold(query);
        var words = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return LocalMatches.None;
        var phrase = string.Join(' ', words);

        var ranked = new Dictionary<string, int>();
        var matches = new List<LocalFile>();
        foreach (var file in files)
        {
            if (file.IsMissing || file.Tags is not { } tags)
                continue;
            var title = Fold(tags.Title ?? paths.NameWithoutExtension(file.Path));
            var rest = Fold($"{tags.Artist} {tags.AlbumArtist} {tags.Album}");
            if (!words.All(w => title.Contains(w, StringComparison.Ordinal) || rest.Contains(w, StringComparison.Ordinal)))
                continue;

            ranked[file.Id] =
                title == phrase ? 0
                : title.StartsWith(phrase, StringComparison.Ordinal) ? 1
                : words.All(w => title.Contains(w, StringComparison.Ordinal)) ? 2
                : 3;
            matches.Add(file);
        }

        // OrderBy is stable, so each rank keeps the library's order.
        var best = LocalTracks.InLibraryOrder(matches, paths)
            .OrderBy(f => ranked[f.Id])
            .Take(limit)
            .ToList();
        return new LocalMatches(best, matches.Count);
    }

    /// <summary>Lower case, without accents, with runs of white space as one space.</summary>
    internal static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var space = true;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsWhiteSpace(c))
            {
                if (!space)
                    builder.Append(' ');
                space = true;
                continue;
            }
            builder.Append(char.ToLowerInvariant(c));
            space = false;
        }
        return builder.ToString().TrimEnd();
    }
}
