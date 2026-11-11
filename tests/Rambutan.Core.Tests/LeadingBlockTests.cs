using Rambutan.Core.Home;
using Rambutan.Core.Models;
using Rambutan.Core.Stats;

namespace Rambutan.Core.Tests;

public class LeadingBlockTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static int _ids;

    private static CatalogEntry Album(string title) =>
        new AlbumEntry { Title = title, BrowseId = "MPRE" + title };

    private static Shelf ShelfOf(string title, int count, ShelfKind kind = ShelfKind.Default) =>
        new(title, [.. Enumerable.Range(1, count).Select(i => Album($"{title} {i}"))]) { Kind = kind };

    private static PlayRecord Play(
        int minutesAfterStart, string? albumId, string album = "Album", double listenedSeconds = 60,
        string videoId = "aaaaaaaaaaa", double durationMinutes = 4, string? thumbnail = null) => new()
    {
        Id = (++_ids).ToString(),
        StartedAt = Start.AddMinutes(minutesAfterStart),
        Listened = TimeSpan.FromSeconds(listenedSeconds),
        VideoId = videoId,
        Title = "Song",
        Artists = [new ArtistRef("Artist", "UCartist")],
        Album = album,
        AlbumBrowseId = albumId,
        Duration = TimeSpan.FromMinutes(durationMinutes),
        ThumbnailUrl = thumbnail,
    };

    [Theory]
    [InlineData("Listen again", true)]
    [InlineData("Quick picks", true)]
    [InlineData("  quick PICKS ", true)]
    [InlineData("New releases", false)]
    [InlineData("Listen again later", false)]
    public void Only_YouTube_Musics_own_titles_mark_the_shelf(string title, bool marked) =>
        Assert.Equal(marked, LeadingBlock.IsRevisitTitle(title));

    [Fact]
    public void The_marked_shelf_becomes_the_tiles_and_leaves_the_shelves()
    {
        var first = ShelfOf("Mixed for you", 5);
        var revisit = ShelfOf("Listen again", 6, ShelfKind.Revisit);
        var last = ShelfOf("New releases", 5);

        var (block, shelves) = LeadingBlock.FromHome([first, revisit, last]);

        Assert.NotNull(block);
        Assert.Equal(revisit.Entries, block.Tiles);
        Assert.Equal("Listen again", block.ShelfTitle);
        Assert.Equal([first, last], shelves);
    }

    [Fact]
    public void Without_the_marked_shelf_there_is_no_block_and_the_shelves_stay()
    {
        var shelves = new[] { ShelfOf("Mixed for you", 5), ShelfOf("Listen again", 6) };

        var (block, left) = LeadingBlock.FromHome(shelves);

        Assert.Null(block);
        Assert.Equal(shelves, left);
    }

    [Fact]
    public void A_marked_shelf_with_too_few_entries_stays_a_shelf()
    {
        var revisit = ShelfOf("Quick picks", LeadingBlock.MinTiles - 1, ShelfKind.Revisit);

        var (block, shelves) = LeadingBlock.FromHome([revisit]);

        Assert.Null(block);
        Assert.Equal([revisit], shelves);
    }

    [Fact]
    public void The_marked_shelf_gives_at_most_two_rows_of_tiles()
    {
        var revisit = ShelfOf("Listen again", LeadingBlock.MaxTiles + 5, ShelfKind.Revisit);

        var (block, shelves) = LeadingBlock.FromHome([revisit]);

        Assert.Equal(revisit.Entries.Take(LeadingBlock.MaxTiles), block!.Tiles);
        Assert.Empty(shelves);
    }

    [Fact]
    public void Plays_give_their_albums_newest_first_once_each()
    {
        var plays = new[]
        {
            Play(0, "MPREa", "A"),
            Play(1, "MPREb", "B"),
            Play(2, "MPREc", "C"),
            Play(3, "MPREa", "A"),
            Play(4, "MPREd", "D"),
        };

        var block = LeadingBlock.FromPlays(plays);

        Assert.NotNull(block);
        Assert.Null(block.ShelfTitle);
        var albums = block.Tiles.Cast<AlbumEntry>().ToList();
        Assert.Equal(["MPREd", "MPREa", "MPREc", "MPREb"], albums.Select(a => a.BrowseId));
        Assert.Equal(["D", "A", "C", "B"], albums.Select(a => a.Title));
        Assert.All(albums, a => Assert.Equal("Artist", a.Subtitle));
    }

    [Fact]
    public void Plays_that_dont_count_and_plays_without_an_album_page_are_left_out()
    {
        var plays = new[]
        {
            Play(0, "MPREa", "A"),
            Play(1, "MPREb", "B"),
            Play(2, "MPREc", "C"),
            Play(3, "MPREd", "D"),
            Play(4, "MPREshort", "Short", listenedSeconds: 10),
            Play(5, "MPRElong", "Long mix", listenedSeconds: 3600, durationMinutes: 60),
            Play(6, null, "Local album", videoId: "local:7"),
            Play(7, null, "No page"),
        };

        var block = LeadingBlock.FromPlays(plays);

        Assert.Equal(["MPREd", "MPREc", "MPREb", "MPREa"], block!.Tiles.Cast<AlbumEntry>().Select(a => a.BrowseId));
    }

    [Fact]
    public void An_album_takes_the_cover_of_its_latest_play_that_has_one()
    {
        var plays = new[]
        {
            Play(0, "MPREa", "A", thumbnail: "https://lh3.googleusercontent.com/old"),
            Play(1, "MPREa", "A", thumbnail: "https://lh3.googleusercontent.com/new"),
            Play(2, "MPREa", "A"),
            Play(3, "MPREb", "B"),
            Play(4, "MPREc", "C"),
            Play(5, "MPREd", "D"),
        };

        var block = LeadingBlock.FromPlays(plays);

        Assert.Equal("https://lh3.googleusercontent.com/new",
            block!.Tiles.Single(t => t.Title == "A").ThumbnailUrl);
    }

    [Fact]
    public void Fewer_albums_than_the_minimum_make_no_block()
    {
        var plays = Enumerable.Range(0, LeadingBlock.MinTiles - 1).Select(i => Play(i, $"MPRE{i}")).ToList();

        Assert.Null(LeadingBlock.FromPlays(plays));
        Assert.Null(LeadingBlock.FromPlays([]));
    }

    [Fact]
    public void Plays_give_at_most_two_rows_of_albums()
    {
        var plays = Enumerable.Range(0, LeadingBlock.MaxTiles + 3).Select(i => Play(i, $"MPRE{i}")).ToList();

        var block = LeadingBlock.FromPlays(plays);

        Assert.Equal(LeadingBlock.MaxTiles, block!.Tiles.Count);
        Assert.Equal($"MPRE{LeadingBlock.MaxTiles + 2}", ((AlbumEntry)block.Tiles[0]).BrowseId);
    }
}
