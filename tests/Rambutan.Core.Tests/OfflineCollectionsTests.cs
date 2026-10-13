using Rambutan.Core.Catalog;
using Rambutan.Core.Downloads;
using Rambutan.Core.Models;

namespace Rambutan.Core.Tests;

public class OfflineCollectionsTests
{
    private sealed class MemoryStore : IKeptCollectionStore
    {
        public List<KeptCollection> Saved { get; set; } = [];
        public int Saves { get; private set; }

        public IReadOnlyList<KeptCollection> Load() => Saved.ToArray();

        public void Save(IReadOnlyList<KeptCollection> collections)
        {
            Saved = [.. collections];
            Saves++;
        }
    }

    private static TrackInfo Track(string id) => new() { VideoId = id, Title = id, Artists = [] };

    private static KeptCollection Copy(
        KeptCollectionKind kind, string id, DateTimeOffset at, params string[] videoIds) =>
        new(kind, id, "Title " + id, "Artist", null, "Album • 2015", "https://remote/" + id,
            videoIds.Select(Track).ToArray(), at);

    private static readonly DateTimeOffset Day1 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = Day1.AddDays(1);

    [Fact]
    public void Keep_ReplacesAnOlderCopyOfThePage_AndListsNewestFirst()
    {
        var store = new MemoryStore();
        var downloaded = new HashSet<string> { "a", "b", "c" };
        var kept = new OfflineCollections(store, downloaded.Contains, _ => false);

        kept.Keep(Copy(KeptCollectionKind.Album, "x", Day1, "a"));
        kept.Keep(Copy(KeptCollectionKind.Album, "y", Day1, "b"));
        kept.Keep(Copy(KeptCollectionKind.Album, "x", Day2, "a", "c"));

        Assert.Equal(["x", "y"], kept.List(KeptCollectionKind.Album).Select(c => c.Id));
        Assert.Equal(2, kept.Find(KeptCollectionKind.Album, "x")!.Tracks.Count);
        Assert.Equal(2, store.Saved.Count);
    }

    [Fact]
    public void FindAndList_OfferOnlyCopiesWithSomethingDownloaded_OfTheirKind()
    {
        var store = new MemoryStore
        {
            Saved =
            [
                Copy(KeptCollectionKind.Playlist, "p", Day1, "a", "q"),
                Copy(KeptCollectionKind.Album, "p", Day1, "a"),
                Copy(KeptCollectionKind.Playlist, "queued", Day1, "q"),
            ],
        };
        var kept = new OfflineCollections(store, id => id == "a", id => id == "q");

        Assert.Equal(["p"], kept.List(KeptCollectionKind.Playlist).Select(c => c.Id));
        Assert.Equal(KeptCollectionKind.Album, kept.Find(KeptCollectionKind.Album, "p")!.Kind);
        // Its only track is still waiting: nothing on the page plays offline yet.
        Assert.Null(kept.Find(KeptCollectionKind.Playlist, "queued"));
    }

    [Fact]
    public void Prune_DropsCopiesWithNothingDownloadedOrWaiting_AndSavesOnlyThen()
    {
        var store = new MemoryStore
        {
            Saved =
            [
                Copy(KeptCollectionKind.Album, "gone", Day1, "x"),
                Copy(KeptCollectionKind.Album, "queued", Day1, "q"),
                Copy(KeptCollectionKind.Playlist, "kept", Day1, "a"),
            ],
        };
        var kept = new OfflineCollections(store, id => id == "a", id => id == "q");

        kept.Prune();
        kept.Prune();

        Assert.Equal(["queued", "kept"], store.Saved.Select(c => c.Id));
        Assert.Equal(1, store.Saves);
    }
}

public class OfflineCatalogTests
{
    private sealed class MemoryStore(params KeptCollection[] saved) : IKeptCollectionStore
    {
        public IReadOnlyList<KeptCollection> Load() => saved;

        public void Save(IReadOnlyList<KeptCollection> collections)
        {
        }
    }

    private static readonly DateTimeOffset KeptAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static KeptCollection Copy(KeptCollectionKind kind, string id, params string[] videoIds) =>
        new(kind, id, "Title " + id, "Adele", "UCadele", "Album • 2015", "https://remote/" + id,
            videoIds.Select(v => new TrackInfo { VideoId = v, Title = v, Artists = [] }).ToArray(), KeptAt);

    private static (OfflineCatalog Catalog, TrackRatingsTests.RatingCatalog Inner) Offline(
        Func<string, string?>? cover = null, params KeptCollection[] copies)
    {
        var inner = new TrackRatingsTests.RatingCatalog { PageFailure = new HttpRequestException("offline") };
        var kept = new OfflineCollections(new MemoryStore(copies), id => id != "notDownloaded", _ => false);
        return (new OfflineCatalog(inner, kept, cover), inner);
    }

    [Fact]
    public async Task Album_FallsBackToItsCopy_ReadOnly_WithTheFirstLocalCover()
    {
        var (catalog, _) = Offline(
            id => id == "t2" ? "file:///covers/t2.jpg" : null,
            Copy(KeptCollectionKind.Album, "MPREb_25", "t1", "t2"));

        var page = await catalog.GetAlbumAsync("MPREb_25");

        Assert.Equal(KeptAt, page.KeptAt);
        Assert.Equal("Title MPREb_25", page.Title);
        Assert.Equal("Adele", page.Artist);
        Assert.Equal(["t1", "t2"], page.Tracks.Select(t => t.VideoId));
        Assert.Equal("file:///covers/t2.jpg", page.ThumbnailUrl);
        // Unknown library state hides the Save toggle, rather than reading "not saved".
        Assert.Null(page.InLibrary);
    }

    [Fact]
    public async Task Playlist_FallsBackToItsCopy_NeverEditable_WithNoMorePages()
    {
        var (catalog, _) = Offline(null, Copy(KeptCollectionKind.Playlist, "PL1", "t1"));

        var page = await catalog.GetPlaylistAsync("PL1");

        Assert.Equal(KeptAt, page.KeptAt);
        Assert.False(page.IsEditable);
        Assert.Null(page.Continuation);
        Assert.Equal("https://remote/PL1", page.ThumbnailUrl); // no local cover: the remote one
        Assert.Equal(new ArtistRef("Adele", "UCadele"), page.Author); // the owner, kept as the artist
    }

    [Fact]
    public async Task WithoutACopy_OrOnCancellation_TheFailureStands()
    {
        var (catalog, inner) = Offline(null, Copy(KeptCollectionKind.Album, "a", "t1"));

        await Assert.ThrowsAsync<HttpRequestException>(() => catalog.GetAlbumAsync("other"));
        await Assert.ThrowsAsync<HttpRequestException>(() => catalog.GetPlaylistAsync("a")); // an album's id, not a playlist's

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        inner.PageFailure = new OperationCanceledException(cts.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.GetAlbumAsync("a", cts.Token));
    }

    [Fact]
    public async Task LibraryTabs_ListTheCopies_ButNotWhenSignedOut()
    {
        var (catalog, inner) = Offline(null,
            Copy(KeptCollectionKind.Playlist, "PL1", "t1"),
            Copy(KeptCollectionKind.Album, "MPREb_25", "t2"),
            Copy(KeptCollectionKind.Album, "nothing", "notDownloaded"));

        var playlists = await catalog.GetLibraryPlaylistsAsync();
        var albums = await catalog.GetLibraryAlbumsAsync();

        Assert.True(playlists.IsOfflineCopy);
        Assert.Equal("PL1", Assert.IsType<PlaylistEntry>(Assert.Single(playlists.Items)).PlaylistId);
        var album = Assert.IsType<AlbumEntry>(Assert.Single(albums.Items));
        Assert.Equal(("MPREb_25", "Adele"), (album.BrowseId, album.Subtitle));

        inner.PageFailure = new NotSignedInException();
        await Assert.ThrowsAsync<NotSignedInException>(() => catalog.GetLibraryAlbumsAsync());
    }
}
