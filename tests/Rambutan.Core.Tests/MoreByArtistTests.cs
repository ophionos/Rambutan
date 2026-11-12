using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.Core.Tests;

public class MoreByArtistTests
{
    private static AlbumEntry Album(string id) => new() { Title = id, BrowseId = id };

    private static ArtistEntry Artist(string id) => new() { Title = id, BrowseId = id };

    private static TrackInfo Song(string id) => new()
    {
        VideoId = id, Title = id, Artists = [new ArtistRef("Artist")],
    };

    private static ArtistPage Page(params Shelf[] shelves) => new()
    {
        BrowseId = "UC1", Name = "Artist", TopSongs = [Song("a"), Song("b")], Shelves = shelves,
    };

    [Fact]
    public void TakesTheThreeShelves_InItsOwnOrder_AndLeavesTheOthersOut()
    {
        var more = MoreByArtist.From(Page(
            new Shelf("Fans might also like", [Artist("UC2")]),
            new Shelf("Videos", [Album("V1")]),
            new Shelf("Singles & EPs", [Album("S1")]),
            new Shelf("Featured on", [Album("F1")]),
            new Shelf("Albums", [Album("A1")])), "A0");

        Assert.Equal(["Albums", "Singles & EPs", "Fans might also like"], more.Shelves.Select(s => s.Title));
        Assert.Equal(2, more.TopSongs.Count);
        Assert.False(more.IsEmpty);
    }

    [Fact]
    public void TheShownAlbumIsLeftOut()
    {
        var more = MoreByArtist.From(Page(new Shelf("Albums", [Album("A1"), Album("A2"), Album("A3")])), "A2");

        var shelf = Assert.Single(more.Shelves);
        Assert.Equal(["A1", "A3"], shelf.Entries.Cast<AlbumEntry>().Select(a => a.BrowseId));
    }

    [Fact]
    public void AShelfThatHeldOnlyTheShownAlbumIsDropped()
    {
        var more = MoreByArtist.From(Page(
            new Shelf("Singles & EPs", [Album("S1")]),
            new Shelf("Albums", [Album("A1")])), "S1");

        Assert.Equal(["Albums"], more.Shelves.Select(s => s.Title));
    }

    [Fact]
    public void AShelfWithoutTheShownAlbumIsKeptAsItCame()
    {
        var albums = new Shelf("Albums", [Album("A1")]) { Kind = ShelfKind.Default };

        var more = MoreByArtist.From(Page(albums), "A9");

        Assert.Same(albums, Assert.Single(more.Shelves));
    }

    [Fact]
    public void NoSongsAndNoneOfTheShelves_IsEmpty()
    {
        var artist = Page(new Shelf("Videos", [Album("V1")])) with { TopSongs = [] };

        Assert.True(MoreByArtist.From(artist, "A1").IsEmpty);
    }

    [Fact]
    public void TopSongsAlone_AreNotEmpty()
    {
        var more = MoreByArtist.From(Page(), "A1");

        Assert.Empty(more.Shelves);
        Assert.False(more.IsEmpty);
    }
}
