namespace Rambutan.Core.Local;

/// <summary>
/// The album picture a folder keeps beside its tracks, for the tracks that
/// embed no cover. The maintainer's decision: an embedded cover always wins.
/// The image is copied into the cover cache like an embedded one, so a drive
/// that goes doesn't take the picture with it.
/// </summary>
public static class LocalFolderCovers
{
    private static readonly string[] Names = ["cover", "folder", "front"];
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png"];

    /// <summary>A picture file larger than this is left alone: no album cover
    /// needs it, and the whole file is copied.</summary>
    public const long MaxBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Whether a file name is one a folder's picture can have: <c>cover</c>,
    /// <c>folder</c>, <c>front</c> or <c>AlbumArt…</c> (Windows Media Player's),
    /// as JPEG or PNG, in any case.
    /// </summary>
    public static bool IsCandidate(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return false;
        var name = Path.GetFileNameWithoutExtension(fileName);
        return Names.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.StartsWith("albumart", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The folder's picture among its candidates (<see cref="IsCandidate"/>):
    /// by name in the order cover, folder, front, then AlbumArt, its large one
    /// before its small one; by extension in the order JPEG, PNG. Null when
    /// none is a candidate or small enough.
    /// </summary>
    public static FoundFile? Pick(IEnumerable<FoundFile> files) =>
        files.Where(f => IsCandidate(Path.GetFileName(f.Path)) && f.Size is > 0 and <= MaxBytes)
            .OrderBy(f => Rank(Path.GetFileNameWithoutExtension(f.Path)))
            .ThenBy(f => Array.FindIndex(Extensions, e => e.Equals(Path.GetExtension(f.Path), StringComparison.OrdinalIgnoreCase)))
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Cast<FoundFile?>()
            .FirstOrDefault();

    private static int Rank(string name)
    {
        var index = Array.FindIndex(Names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            return index;
        if (name.Contains("large", StringComparison.OrdinalIgnoreCase))
            return Names.Length;
        return name.Contains("small", StringComparison.OrdinalIgnoreCase) ? Names.Length + 2 : Names.Length + 1;
    }

    /// <summary>
    /// What a scan does with a file's cover when its folder's picture is
    /// <paramref name="picture"/>: true when the file should take it (or lose
    /// the one it took, for null). A file that embeds a cover never changes here;
    /// <paramref name="fromFolder"/> is the picture it took before, null when its
    /// cover is embedded or it has none.
    /// </summary>
    public static bool NeedsUpdate(bool hasCover, FoundFile? fromFolder, FoundFile? picture) =>
        hasCover && fromFolder is null ? false : fromFolder != picture;
}
