using Rambutan.Core.Downloads;
using Rambutan.Core.Models;

namespace Rambutan.Core.Local;

/// <summary>The filters of Library → On this computer.</summary>
public enum LocalSource
{
    /// <summary>The watched folders' files and the downloads, each song once.</summary>
    All,

    /// <summary>The watched folders' files, without the downloads kept there.</summary>
    Folders,

    /// <summary>The downloads, missing ones too.</summary>
    Downloads,
}

/// <summary>A row of On this computer: a watched folder's file, playing under
/// its <c>local:</c> id, or a download, playing under its YouTube id.</summary>
/// <param name="Track">What plays: <see cref="LocalTracks.ToTrack"/> for a file,
/// the download's own track for a download.</param>
public sealed record LocalListItem(TrackInfo Track, LocalFile? File = null, DownloadedTrack? Download = null);

public static class LocalListing
{
    /// <summary>
    /// The rows a filter lists. Missing and unreadable files are never listed,
    /// and a download whose file is gone is listed only under
    /// <see cref="LocalSource.Downloads"/>, which keeps the downloads' own order
    /// (<see cref="IDownloadStore.List"/>). The others list in library order, a
    /// download by its track's names. Under <see cref="LocalSource.All"/>, a file
    /// whose tags name a video that has a download there (an export of it) is
    /// listed as that download, so the song shows once.
    /// </summary>
    /// <param name="files">The watched folders' files (<c>LocalFileIndex.Files</c>);
    /// an entry that is a download is left out, since the downloads list it.</param>
    public static IReadOnlyList<LocalListItem> For(
        LocalSource source, IEnumerable<LocalFile> files, IReadOnlyList<DownloadedTrack> downloads,
        LocalPaths? paths = null)
    {
        paths ??= LocalPaths.Platform;
        if (source == LocalSource.Downloads)
            return downloads.Select(d => new LocalListItem(d.Track, Download: d)).ToList();

        var listed = files.Where(f => f is { Download: null, IsMissing: false, IsUnreadable: false });
        if (source == LocalSource.Folders)
            return Order(listed.Select(f => new LocalListItem(LocalTracks.ToTrack(f, paths), f)), paths);

        var present = downloads.Where(d => !d.IsMissing).ToList();
        var downloaded = present.Select(d => d.Track.VideoId).ToHashSet(StringComparer.Ordinal);
        return Order(listed
            .Where(f => f.Tags?.YouTubeVideoId is not { } videoId || !downloaded.Contains(videoId))
            .Select(f => new LocalListItem(LocalTracks.ToTrack(f, paths), f))
            .Concat(present.Select(d => new LocalListItem(d.Track, Download: d))), paths);
    }

    private static IReadOnlyList<LocalListItem> Order(IEnumerable<LocalListItem> items, LocalPaths paths) =>
        LocalTracks.InLibraryOrder(items, item => item.File is { } file
            ? LocalTracks.OrderKey(file, item.Track)
            // A download has no tags of its own here; its id breaks the last tie.
            : new LibraryOrderKey(item.Track.Artists.FirstOrDefault()?.Name, item.Track.Album,
                null, null, item.Track.Title, item.Track.VideoId), paths);
}
