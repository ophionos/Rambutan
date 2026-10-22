using Rambutan.Core.Local;

namespace Rambutan.Core.Tests;

public class LocalFolderCoversTests
{
    private static FoundFile Picture(string path, long size = 100) => new(path, size, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("cover.jpg", true)]
    [InlineData("Folder.JPG", true)]
    [InlineData("front.jpeg", true)]
    [InlineData("FRONT.png", true)]
    [InlineData("AlbumArtSmall.jpg", true)]
    [InlineData("AlbumArt_{4F1E}_Large.jpg", true)]
    [InlineData("back.jpg", false)]
    [InlineData("cover.gif", false)]
    [InlineData("cover", false)]
    [InlineData("my cover.jpg", false)]
    public void IsCandidate(string name, bool expected) => Assert.Equal(expected, LocalFolderCovers.IsCandidate(name));

    [Fact]
    public void Pick_GoesByNameThenByType()
    {
        Assert.Equal("/a/cover.png", LocalFolderCovers.Pick(
            [Picture("/a/front.jpg"), Picture("/a/cover.png"), Picture("/a/folder.jpg")])?.Path);
        Assert.Equal("/a/folder.jpg", LocalFolderCovers.Pick([Picture("/a/folder.png"), Picture("/a/folder.jpg")])?.Path);
        Assert.Equal("/a/AlbumArt_{X}_Large.jpg", LocalFolderCovers.Pick(
            [Picture("/a/AlbumArtSmall.jpg"), Picture("/a/AlbumArt_{X}_Large.jpg")])?.Path);
        Assert.Equal("/a/front.jpg", LocalFolderCovers.Pick([Picture("/a/AlbumArt_{X}_Large.jpg"), Picture("/a/front.jpg")])?.Path);
    }

    [Fact]
    public void Pick_LeavesOutEmptyAndHugePictures()
    {
        Assert.Null(LocalFolderCovers.Pick([Picture("/a/cover.jpg", 0)]));
        Assert.Equal("/a/folder.jpg", LocalFolderCovers.Pick(
            [Picture("/a/cover.jpg", LocalFolderCovers.MaxBytes + 1), Picture("/a/folder.jpg")])?.Path);
        Assert.Null(LocalFolderCovers.Pick([Picture("/a/notes.jpg")]));
    }

    [Fact]
    public void NeedsUpdate_NeverTouchesAnEmbeddedCover()
    {
        var picture = Picture("/a/cover.jpg");
        var changed = picture with { Size = 200 };

        Assert.False(LocalFolderCovers.NeedsUpdate(hasCover: true, fromFolder: null, picture)); // embedded
        Assert.True(LocalFolderCovers.NeedsUpdate(hasCover: false, fromFolder: null, picture)); // none yet
        Assert.False(LocalFolderCovers.NeedsUpdate(hasCover: true, fromFolder: picture, picture));
        Assert.True(LocalFolderCovers.NeedsUpdate(hasCover: true, fromFolder: picture, changed));
        Assert.True(LocalFolderCovers.NeedsUpdate(hasCover: true, fromFolder: picture, null)); // removed
        Assert.False(LocalFolderCovers.NeedsUpdate(hasCover: false, fromFolder: null, null));
    }

    [Fact]
    public void AFolderPicture_CountsForTheWatcher()
    {
        var linux = new LocalPaths(ignoreCase: false, '/');

        Assert.True(LocalWatch.Counts(WatcherChange.Created, "/m/a/cover.jpg", false, [], linux));
        Assert.True(LocalWatch.Counts(WatcherChange.Changed, "/m/a/Folder.png", false, [], linux));
        Assert.True(LocalWatch.Counts(WatcherChange.DeletedOrRenamed, "/m/a/front.jpg", false, [], linux));
        Assert.False(LocalWatch.Counts(WatcherChange.Created, "/m/a/back.jpg", false, [], linux));
    }
}
