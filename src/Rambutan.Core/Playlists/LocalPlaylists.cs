using Rambutan.Core.Local;
using Rambutan.Core.Models;

namespace Rambutan.Core.Playlists;

/// <summary>The rules of the user's local playlists, without I/O.</summary>
public static class LocalPlaylists
{
    /// <summary>How far two durations may differ and still be the same file's.</summary>
    internal static readonly TimeSpan SameLength = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Appends the items the playlist doesn't hold yet, in their order; an item
    /// it holds, or one given twice, is skipped (maintainer's decision: adding a
    /// song that is there adds nothing). Returns the playlist and how many were added.
    /// </summary>
    public static (LocalPlaylist Playlist, int Added) Add(
        LocalPlaylist playlist, IEnumerable<LocalPlaylistItem> items, DateTimeOffset now)
    {
        var held = playlist.Items.Select(i => i.Track.VideoId).ToHashSet(StringComparer.Ordinal);
        var added = items.Where(i => held.Add(i.Track.VideoId)).ToList();
        return added.Count == 0
            ? (playlist, 0)
            : (playlist with { Items = [.. playlist.Items, .. added], ChangedAt = now }, added.Count);
    }

    /// <summary>Removes the row at <paramref name="index"/>; a copy can hold a
    /// song twice, so a row is its place, not its id. Out of range changes nothing.</summary>
    public static LocalPlaylist RemoveAt(LocalPlaylist playlist, int index, DateTimeOffset now)
    {
        if (index < 0 || index >= playlist.Items.Count)
            return playlist;
        var items = playlist.Items.ToList();
        items.RemoveAt(index);
        return playlist with { Items = items, ChangedAt = now };
    }

    /// <summary>Moves the row at <paramref name="from"/> so it ends up at
    /// <paramref name="to"/>. Out of range, or no move, changes nothing.</summary>
    public static LocalPlaylist Move(LocalPlaylist playlist, int from, int to, DateTimeOffset now)
    {
        var count = playlist.Items.Count;
        if (from == to || from < 0 || from >= count || to < 0 || to >= count)
            return playlist;
        var items = playlist.Items.ToList();
        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
        return playlist with { Items = items, ChangedAt = now };
    }

    /// <summary>The name as it is kept: trimmed; null when nothing is left.</summary>
    public static string? CleanName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>
    /// A new playlist holding <paramref name="tracks"/> as they are, duplicates
    /// included (maintainer's decision: a copy keeps what it was copied from).
    /// </summary>
    public static LocalPlaylist Copy(
        string id, string name, IEnumerable<TrackInfo> tracks, string copiedFrom, DateTimeOffset now) =>
        new(id, name, tracks.Select(LocalPlaylistItem.Of).ToList(), now, now, copiedFrom);

    /// <summary>
    /// How a row shows now. A YouTube Music song shows as it is. A local file shows
    /// as its file in the index when that is present; else as the present file at
    /// its path; else as the one present file whose tags are its tags (title,
    /// first artist and album, ignoring case, and a length within a second): a
    /// file moved or renamed gets a new id, and is found again. Else it is missing.
    /// </summary>
    /// <param name="find">The index's file by id, present or not.</param>
    /// <param name="present">The present, readable files that can be found again.</param>
    public static ResolvedPlaylistItem Resolve(
        LocalPlaylistItem item, Func<string, LocalFile?> find, IReadOnlyList<LocalFile> present,
        LocalPaths? paths = null)
    {
        if (!item.IsLocal)
            return new(item, IsMissing: false);
        paths ??= LocalPaths.Platform;

        if (find(item.Track.VideoId) is { IsMissing: false, IsUnreadable: false } file)
            return new(LocalPlaylistItem.Of(file), IsMissing: false);
        if (item.Path is { } path && present.FirstOrDefault(f => paths.Comparer.Equals(f.Path, path)) is { } atPath)
            return new(LocalPlaylistItem.Of(atPath), IsMissing: false);
        if (item.Track.Title.Length > 0 && SameTags(item.Track, present, paths) is { } moved)
            return new(LocalPlaylistItem.Of(moved), IsMissing: false);
        return new(item, IsMissing: true);
    }

    /// <summary>
    /// <see cref="Resolve"/> for every row, and the playlist to keep when a row
    /// changed (a file found again, or its tags edited); null when none did.
    /// </summary>
    public static (IReadOnlyList<ResolvedPlaylistItem> Rows, LocalPlaylist? Changed) ResolveAll(
        LocalPlaylist playlist, Func<string, LocalFile?> find, IReadOnlyList<LocalFile> present,
        LocalPaths? paths = null)
    {
        var rows = playlist.Items.Select(i => Resolve(i, find, present, paths)).ToList();
        var changed = rows.Where((row, i) => !SameItem(row.Item, playlist.Items[i])).Any();
        // Not a change the user made, so ChangedAt stays.
        return (rows, changed ? playlist with { Items = rows.Select(r => r.Item).ToList() } : null);
    }

    // TrackInfo's artists compare by reference, so a record's own equality would
    // see every resolved row as changed.
    private static bool SameItem(LocalPlaylistItem a, LocalPlaylistItem b) =>
        a.Track.VideoId == b.Track.VideoId && a.Path == b.Path && a.Track.Title == b.Track.Title &&
        a.Track.Album == b.Track.Album && a.Track.Duration == b.Track.Duration &&
        a.Track.Artists.Select(x => x.Name).SequenceEqual(b.Track.Artists.Select(x => x.Name));

    private static LocalFile? SameTags(TrackInfo track, IReadOnlyList<LocalFile> present, LocalPaths paths)
    {
        var artist = track.Artists.FirstOrDefault()?.Name;
        LocalFile? found = null;
        foreach (var file in present)
        {
            // Only a title tag counts: a file named after its title isn't its tags.
            if (file.Tags?.Title is null)
                continue;
            var candidate = LocalTracks.ToTrack(file, paths);
            if (!Same(candidate.Title, track.Title) ||
                !Same(candidate.Artists.FirstOrDefault()?.Name, artist) ||
                !Same(candidate.Album, track.Album) ||
                !SameDuration(candidate.Duration, track.Duration))
                continue;
            if (found is not null)
                return null; // two files fit: neither is known to be it
            found = file;
        }
        return found;
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameDuration(TimeSpan? a, TimeSpan? b) =>
        a is null || b is null ? a is null && b is null : (a.Value - b.Value).Duration() <= SameLength;
}
