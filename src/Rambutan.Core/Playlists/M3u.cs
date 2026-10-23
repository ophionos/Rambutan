using System.Globalization;
using System.Text;
using Rambutan.Core.Local;

namespace Rambutan.Core.Playlists;

/// <summary>An entry of an M3U playlist: where it points, with the title and
/// length of the <c>#EXTINF</c> line before it, when there was one.</summary>
public sealed record M3uEntry(string Location, string? Title = null, TimeSpan? Duration = null);

/// <summary>
/// M3U playlists, to move local playlists to and from other players. An
/// <c>.m3u8</c> is UTF-8; a plain <c>.m3u</c> is decoded as an <c>.lrc</c> is
/// (<see cref="Text.FileText"/>). Rules decided by the maintainer: on import a
/// path that isn't there becomes a missing row and a URL is skipped; on export
/// paths are relative to the playlist's folder where they can be, and YouTube
/// Music songs are left out.
/// </summary>
public static class M3u
{
    public const string Extension = ".m3u8";

    /// <summary>The entries in order, duplicates included. Comments and other
    /// directives are skipped.</summary>
    public static IReadOnlyList<M3uEntry> Parse(string text)
    {
        var entries = new List<M3uEntry>();
        string? title = null;
        TimeSpan? duration = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0)
                continue;
            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                    (duration, title) = Info(line["#EXTINF:".Length..]);
                continue;
            }
            entries.Add(new M3uEntry(line, title, duration));
            (title, duration) = (null, null);
        }
        return entries;
    }

    // "<seconds>[ key="value"…],<title>": -1 seconds is an unknown length.
    private static (TimeSpan?, string?) Info(string info)
    {
        var comma = info.IndexOf(',');
        var head = (comma < 0 ? info : info[..comma]).Trim();
        if (head.IndexOf(' ') is var space and >= 0)
            head = head[..space];
        var title = comma < 0 ? null : info[(comma + 1)..].Trim();
        TimeSpan? duration = double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                             && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : null;
        return (duration, string.IsNullOrEmpty(title) ? null : title);
    }

    /// <summary>
    /// The full path an entry names, a relative one taken from
    /// <paramref name="folder"/>, the playlist file's folder. A <c>file:</c> URL
    /// is the path it names. Null for any other URL, which a local playlist
    /// can't hold.
    /// </summary>
    public static string? Locate(string location, string folder, LocalPaths paths)
    {
        if (location.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.IsFile
                ? paths.Full(uri.LocalPath, folder)
                : null;
        if (IsUrl(location))
            return null;
        // A playlist written on Windows separates with backslashes, which are
        // ordinary characters elsewhere.
        if (paths.Separator == '/' && !location.Contains('/'))
            location = location.Replace('\\', '/');
        return paths.Full(location, folder);
    }

    // A scheme of two letters or more, so "C:\" is a path.
    private static bool IsUrl(string location)
    {
        var colon = location.IndexOf("://", StringComparison.Ordinal);
        return colon >= 2 && char.IsAsciiLetter(location[0]) &&
               location.AsSpan(0, colon).IndexOfAnyExcept(SchemeChars) < 0;
    }

    private static readonly System.Buffers.SearchValues<char> SchemeChars =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+.-");

    /// <summary>
    /// The playlist as the text of an <c>.m3u8</c> in <paramref name="folder"/>:
    /// each local file at its path relative to the folder where it can be, after
    /// an <c>#EXTINF</c> line with its length and "artist - title". A YouTube
    /// Music song is left out and counted.
    /// </summary>
    public static (string Text, int Written, int LeftOut) Write(
        IEnumerable<LocalPlaylistItem> items, string folder, LocalPaths paths)
    {
        var text = new StringBuilder("#EXTM3U\n");
        int written = 0, leftOut = 0;
        foreach (var item in items)
        {
            if (!item.IsLocal || item.Path is not { } path)
            {
                leftOut++;
                continue;
            }
            var track = item.Track;
            var seconds = track.Duration is { } d ? (long)Math.Round(d.TotalSeconds) : -1;
            var artists = string.Join(", ", track.Artists.Select(a => a.Name));
            var name = artists.Length > 0 ? $"{artists} - {track.Title}" : track.Title;
            text.Append("#EXTINF:").Append(seconds.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(OneLine(name)).Append('\n')
                .Append(paths.Relative(folder, path)).Append('\n');
            written++;
        }
        return (text.ToString(), written, leftOut);
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");
}
