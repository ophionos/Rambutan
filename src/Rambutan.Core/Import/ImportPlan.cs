using Rambutan.Core.Models;

namespace Rambutan.Core.Import;

/// <summary>A playlist an import created, with how many songs it added there.</summary>
public sealed record ImportedPart(string PlaylistId, int Count);

/// <summary>
/// What an import of one list created, kept so a later import of the same
/// list adds only what is new (Update from Spotify).
/// </summary>
/// <param name="ListName">The list's name in the file; empty for Liked Songs.</param>
/// <param name="PlaylistName">The name the playlists were created under; a
/// part after the first adds " (2)", " (3)", ….</param>
/// <param name="Parts">The playlists, in order: more than one when the list
/// was longer than YouTube Music takes in one playlist.</param>
/// <param name="Keys">Every song of the list as it was read
/// (<see cref="ImportMatcher.KeyOf"/>), once per row: the ones added, and the
/// ones not found or left out, so those don't come back as new.</param>
/// <param name="Source">The list's Spotify URI when it was read from a link
/// (<see cref="ImportedList.Source"/>); null for a file's list.</param>
public sealed record ImportRecord(
    string ListName,
    ImportedListKind ListKind,
    ImportTarget Target,
    string PlaylistName,
    IReadOnlyList<ImportedPart> Parts,
    IReadOnlyList<string> Keys,
    DateTimeOffset ImportedAt,
    string? Source = null);

/// <summary>A playlist to create: its name and its songs, in order.</summary>
public sealed record PlannedPlaylist(string Name, IReadOnlyList<TrackInfo> Songs);

/// <summary>The rules for turning a reviewed list into playlists.</summary>
public static class ImportPlan
{
    /// <summary>
    /// The most songs put in one YouTube Music playlist. YouTube Music's help
    /// doesn't state a limit; YouTube's support has given 5,000 for any
    /// playlist, so a longer list is split into parts of this size.
    /// </summary>
    public const int YouTubeMusicCap = 5000;

    /// <summary>The most songs one request adds to a YouTube Music playlist;
    /// one request for thousands is more likely to be refused whole.</summary>
    public const int AddBatch = 100;

    public static int CapOf(ImportTarget target) =>
        target == ImportTarget.YouTubeMusic ? YouTubeMusicCap : int.MaxValue;

    /// <summary>"Name" for the first part, "Name (2)" for the second, ….</summary>
    public static string PartName(string name, int part) => part <= 1 ? name : $"{name} ({part})";

    /// <summary>The playlists for <paramref name="songs"/>: one, or parts of
    /// at most <paramref name="cap"/>; none for no songs.</summary>
    public static IReadOnlyList<PlannedPlaylist> Split(string name, IReadOnlyList<TrackInfo> songs, int cap) =>
        Parts(name, songs, cap, first: 1);

    /// <summary>
    /// Where an update's new songs go: into the last playlist of the earlier
    /// import while it has room, then into new parts named after it.
    /// </summary>
    public static (IReadOnlyList<TrackInfo> ToLast, IReadOnlyList<PlannedPlaylist> NewParts) Append(
        ImportRecord record, IReadOnlyList<TrackInfo> songs, int cap)
    {
        if (record.Parts.Count == 0)
            return ([], Parts(record.PlaylistName, songs, cap, first: 1));
        var room = Math.Max(0, cap - record.Parts[^1].Count);
        var toLast = songs.Take(room).ToList();
        return (toLast, Parts(record.PlaylistName, songs.Skip(room).ToList(), cap, first: record.Parts.Count + 1));
    }

    /// <summary>
    /// The rows of <paramref name="list"/> that are new since <paramref name="record"/>:
    /// each song beyond as many times as the earlier import read it (a song
    /// added twice since comes twice), in the list's order.
    /// </summary>
    public static IReadOnlyList<ImportedTrack> NewSince(ImportedList list, ImportRecord record)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in record.Keys)
            seen[key] = seen.GetValueOrDefault(key) + 1;
        var added = new List<ImportedTrack>();
        foreach (var track in list.Tracks)
        {
            var key = ImportMatcher.KeyOf(track);
            if (seen.GetValueOrDefault(key) is > 0 and var left)
                seen[key] = left - 1;
            else
                added.Add(track);
        }
        return added;
    }

    /// <summary>
    /// The rows an import got through when it stopped after adding
    /// <paramref name="added"/> songs: every row before the next one with a
    /// song to add, so a later import adds the rest and nothing twice.
    /// </summary>
    public static IReadOnlyList<ImportedTrack> Covered(
        IReadOnlyList<ImportedTrack> rows, Func<ImportedTrack, bool> addsSong, int added)
    {
        var covered = new List<ImportedTrack>();
        var seen = 0;
        foreach (var row in rows)
        {
            if (addsSong(row))
            {
                if (seen == added)
                    break;
                seen++;
            }
            covered.Add(row);
        }
        return covered;
    }

    /// <summary>The latest record of <paramref name="list"/> imported to
    /// <paramref name="target"/>: by its link's URI when it was read from one,
    /// else by the file list's kind and name (a link's list renamed on Spotify
    /// stays the same list, and a file's list of the same name isn't it);
    /// null when it wasn't imported there before.</summary>
    public static ImportRecord? EarlierImport(IEnumerable<ImportRecord> records, ImportedList list, ImportTarget target) =>
        records
            .Where(r => r.Target == target && r.Parts.Count > 0 &&
                        (list.Source is not null
                            ? r.Source == list.Source
                            : r.Source is null && r.ListKind == list.Kind && r.ListName == list.Name))
            .MaxBy(r => r.ImportedAt);

    private static List<PlannedPlaylist> Parts(string name, IReadOnlyList<TrackInfo> songs, int cap, int first)
    {
        var parts = new List<PlannedPlaylist>();
        var size = Math.Max(1, cap);
        for (var i = 0; i < songs.Count; i += size)
            parts.Add(new PlannedPlaylist(PartName(name, first + parts.Count), songs.Skip(i).Take(size).ToList()));
        return parts;
    }
}
