using Rambutan.Core.Models;

namespace Rambutan.Core.Downloads;

public enum KeptCollectionKind { Album, Playlist }

/// <summary>
/// An album's or a playlist's page as it was when the user downloaded it: its
/// header and its whole track list, so the page opens without the network.
/// </summary>
/// <param name="Id">The album's browseId or the playlist's id.</param>
/// <param name="Artist">The album's artist, or the playlist's owner.</param>
/// <param name="SubtitleInfo">"Album • 2013", "Playlist • 2026".</param>
/// <param name="KeptAt">When the copy was made, which the offline page says.</param>
public sealed record KeptCollection(
    KeptCollectionKind Kind,
    string Id,
    string Title,
    string? Artist,
    string? ArtistBrowseId,
    string? SubtitleInfo,
    string? ThumbnailUrl,
    IReadOnlyList<TrackInfo> Tracks,
    DateTimeOffset KeptAt);

public interface IKeptCollectionStore
{
    IReadOnlyList<KeptCollection> Load();

    void Save(IReadOnlyList<KeptCollection> collections);
}

/// <summary>
/// The copies of downloaded albums and playlists. "Download all" on a page keeps
/// one; a copy is offered only while at least one of its tracks is
/// downloaded, and goes once none is downloaded or waiting to be.
/// </summary>
/// <param name="isDownloaded">The track has a finished download whose file exists.</param>
/// <param name="isQueued">The track has a download job, whatever its state.</param>
public sealed class OfflineCollections(
    IKeptCollectionStore store, Func<string, bool> isDownloaded, Func<string, bool> isQueued)
{
    private readonly object _lock = new();
    private List<KeptCollection>? _collections;

    /// <summary>Keeps the copy, replacing an older one of the same page.
    /// Call after queueing its tracks, so a prune between the two can't drop it.</summary>
    public void Keep(KeptCollection collection)
    {
        lock (_lock)
        {
            var all = Loaded();
            all.RemoveAll(c => c.Kind == collection.Kind && c.Id == collection.Id);
            all.Insert(0, collection);
            store.Save(all);
        }
    }

    public KeptCollection? Find(KeptCollectionKind kind, string id)
    {
        lock (_lock)
            return Loaded().FirstOrDefault(c => c.Kind == kind && c.Id == id && HasDownloads(c));
    }

    public IReadOnlyList<KeptCollection> List(KeptCollectionKind kind)
    {
        lock (_lock)
            return Loaded().Where(c => c.Kind == kind && HasDownloads(c)).ToArray();
    }

    public void Prune()
    {
        lock (_lock)
        {
            var all = Loaded();
            if (all.RemoveAll(c => !c.Tracks.Any(t => isDownloaded(t.VideoId) || isQueued(t.VideoId))) > 0)
                store.Save(all);
        }
    }

    private bool HasDownloads(KeptCollection c) => c.Tracks.Any(t => isDownloaded(t.VideoId));

    /// <summary>Call under the lock.</summary>
    private List<KeptCollection> Loaded() => _collections ??= [.. store.Load().OrderByDescending(c => c.KeptAt)];
}
