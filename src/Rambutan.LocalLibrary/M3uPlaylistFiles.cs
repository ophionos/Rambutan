using System.Text;
using Rambutan.Core.Local;
using Rambutan.Core.Playlists;
using Rambutan.Core.Text;

namespace Rambutan.LocalLibrary;

/// <summary>What an imported playlist file holds, as rows of a local playlist.</summary>
/// <param name="Name">The file's name without its extension.</param>
/// <param name="Skipped">Entries left out: URLs, and files that are there but
/// aren't audio that can be read.</param>
public sealed record M3uImport(string Name, IReadOnlyList<LocalPlaylistItem> Items, int Skipped);

/// <summary>Reads and writes M3U playlist files (<see cref="M3u"/>), with the
/// local index giving each file its id.</summary>
public static class M3uPlaylistFiles
{
    /// <summary>Larger than any playlist of local files; a file past it isn't one.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The rows <paramref name="file"/> names, in its order, duplicates included.
    /// A file that is there is read into the index (as an opened file outside the
    /// watched folders); one that isn't is remembered under an id of its own and
    /// shows as missing until it comes. Throws <see cref="IOException"/> (and
    /// <see cref="UnauthorizedAccessException"/>) when the playlist can't be read,
    /// <see cref="InvalidDataException"/> when it is too large to be one.
    /// </summary>
    /// <param name="listed">Told how many files are there to read, before any is.</param>
    public static async Task<M3uImport> ReadAsync(
        LocalFileIndex index, string file, Action<int>? listed = null, LocalPaths? paths = null,
        CancellationToken ct = default)
    {
        paths ??= LocalPaths.Platform;
        file = Path.GetFullPath(file);
        var (entries, located, there) = await Task.Run(() =>
        {
            if (new FileInfo(file).Length > MaxBytes)
                throw new InvalidDataException("Too large for a playlist.");
            var bytes = File.ReadAllBytes(file);
            var text = file.EndsWith(M3u.Extension, StringComparison.OrdinalIgnoreCase)
                ? new UTF8Encoding(false).GetString(bytes)
                : FileText.Decode(bytes);
            var entries = M3u.Parse(text);
            var folder = Path.GetDirectoryName(file)!;
            var located = entries.Select(e => M3u.Locate(e.Location, folder, paths)).ToList();
            // A folder named in the list is never opened: its files would come too.
            var there = located.OfType<string>().Distinct(paths.Comparer)
                .ToDictionary(p => p, p => File.Exists(p) ? true : Directory.Exists(p) ? (bool?)null : false, paths.Comparer);
            return (entries, located, there);
        }, ct);

        var opened = (await index.OpenAsync(there.Where(p => p.Value == true).Select(p => p.Key).ToList(), listed, ct))
            .ToDictionary(f => f.Path, paths.Comparer);
        var remembered = index.Remember(there.Where(p => p.Value == false).Select(p => p.Key).ToList())
            .ToDictionary(f => f.Path, paths.Comparer);

        var items = new List<LocalPlaylistItem>(entries.Count);
        var skipped = 0;
        foreach (var path in located)
        {
            if (path is not null && (opened.GetValueOrDefault(path) ?? remembered.GetValueOrDefault(path)) is { } found)
                items.Add(LocalPlaylistItem.Of(found));
            else
                skipped++;
        }
        return new M3uImport(Path.GetFileNameWithoutExtension(file), items, skipped);
    }

    /// <summary>
    /// Writes <paramref name="items"/> to <paramref name="file"/> as an
    /// <c>.m3u8</c> (UTF-8, no byte order mark); how many files it holds, and how
    /// many YouTube Music songs were left out. Throws <see cref="IOException"/>
    /// (and <see cref="UnauthorizedAccessException"/>) when it can't be written.
    /// </summary>
    public static async Task<(int Written, int LeftOut)> WriteAsync(
        IReadOnlyList<LocalPlaylistItem> items, string file, LocalPaths? paths = null, CancellationToken ct = default)
    {
        paths ??= LocalPaths.Platform;
        file = Path.GetFullPath(file);
        var (text, written, leftOut) = M3u.Write(items, Path.GetDirectoryName(file)!, paths);
        await File.WriteAllTextAsync(file, text, new UTF8Encoding(false), ct);
        return (written, leftOut);
    }
}
