using Rambutan.Core.Downloads;
using Rambutan.Core.Local;
using Rambutan.Core.Models;

namespace Rambutan.Core.Tests;

public class LocalListingTests
{
    private static readonly LocalPaths Linux = new(ignoreCase: false, '/');
    private static readonly DateTimeOffset Then = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static LocalFile File(string id, string title, string artist, string? videoId = null) =>
        new(id, $"/m/{title}.mp3", 100, Then,
            new LocalFileTags("mp3", TimeSpan.FromSeconds(200), title, artist, YouTubeVideoId: videoId));

    private static DownloadedTrack Download(string videoId, string title, string artist, bool missing = false) =>
        new(new TrackInfo { VideoId = videoId, Title = title, Artists = [new ArtistRef(artist, "UC" + artist)] },
            1000, Then, missing);

    private static readonly LocalFile[] Files =
    [
        File("local:1", "Zebra", "Cats"),
        File("local:2", "Hello", "Adele", videoId: "YQHsXMglC9A"), // an export of the download below
        File("local:3", "Apple", "Beatles", videoId: "khnokW3Mw24"), // its download is gone
        new("local:4", "/m/gone.mp3", 100, Then, new LocalFileTags("mp3", Title: "Gone"), IsMissing: true),
        new("local:5", "/m/bad.mp3", 100, Then, Tags: null),
        // A download kept in a watched folder: the downloads list it.
        new("local:6", "/m/dl.opus", 100, Then, new LocalFileTags("opus", Title: "Hello"),
            Download: new LocalDownload(Download("YQHsXMglC9A", "Hello", "Adele").Track, "opus", null, null, null, 100, Then)),
    ];

    private static readonly DownloadedTrack[] Downloads =
    [
        Download("YQHsXMglC9A", "Hello", "Adele"),
        Download("khnokW3Mw24", "Apple", "Beatles", missing: true),
        Download("aaaaaaaaaaa", "Bossa", "Bebel"),
    ];

    private static string[] Ids(IReadOnlyList<LocalListItem> items) => [.. items.Select(i => i.Track.VideoId)];

    [Fact]
    public void All_ListsEachSongOnce_InLibraryOrder_WithTheDownloadForItsExport()
    {
        var items = LocalListing.For(LocalSource.All, Files, Downloads, Linux);

        Assert.Equal(["YQHsXMglC9A", "local:3", "aaaaaaaaaaa", "local:1"], Ids(items));
        Assert.NotNull(items[0].Download);
        Assert.Same(Files[2], items[1].File);
    }

    [Fact]
    public void Folders_ListsTheFolderFiles_ExportsToo_AndNoDownload()
    {
        var items = LocalListing.For(LocalSource.Folders, Files, Downloads, Linux);

        Assert.Equal(["local:2", "local:3", "local:1"], Ids(items));
        Assert.All(items, i => Assert.Null(i.Download));
    }

    [Fact]
    public void Downloads_ListsEveryDownload_InTheirOwnOrder_MissingOnesToo()
    {
        var items = LocalListing.For(LocalSource.Downloads, Files, Downloads, Linux);

        Assert.Equal(["YQHsXMglC9A", "khnokW3Mw24", "aaaaaaaaaaa"], Ids(items));
        Assert.True(items[1].Download!.IsMissing);
    }
}
