using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Home;
using Rambutan.Core.Models;
using Rambutan.Core.Stats;

namespace Rambutan.App.Tests;

/// <summary>
/// The tiles above Home's shelves: YouTube Music's own Listen again or Quick
/// picks shelf, else the play log's latest albums; none signed out, with the
/// log off, empty or unreadable. Tiles that come after the shelves show only
/// while the page is at rest at the top.
/// </summary>
public class HomeLeadingBlockTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Setup
    {
        public FakeCatalog Catalog { get; } = new();
        public HomePageViewModel Home { get; }
        public bool SignedIn { get; set; } = true;
        public bool AtRest { get; set; } = true;
        public int Reads { get; private set; }
        public Func<Task<PlayLogContents>>? Log { get; set; }

        public Setup()
        {
            Home = new HomePageViewModel(Catalog, null!)
            {
                IsSignedIn = () => SignedIn,
                IsAtRest = () => AtRest,
                RecentPlays = () =>
                {
                    Reads++;
                    return Log?.Invoke();
                },
            };
        }
    }

    internal static Shelf Revisit(string title = "Quick picks", int count = 6) =>
        new(title, [.. Enumerable.Range(1, count).Select(i => FakeCatalog.Entry($"{title} {i}"))]) { Kind = ShelfKind.Revisit };

    private static Shelf Plain(string title) => new(title, [FakeCatalog.Entry(title)]);

    internal static PlayRecord Play(int minutes, string albumId) => new()
    {
        Id = $"{albumId}-{minutes}",
        StartedAt = Start.AddMinutes(minutes),
        Listened = TimeSpan.FromMinutes(3),
        VideoId = "aaaaaaaaaaa",
        Title = "Song",
        Artists = [new ArtistRef("Artist", "UCartist")],
        Album = "Album " + albumId,
        AlbumBrowseId = albumId,
        Duration = TimeSpan.FromMinutes(4),
    };

    internal static PlayLogContents FourAlbums() =>
        new(PlayLogState.Readable, [Play(0, "MPREa"), Play(1, "MPREb"), Play(2, "MPREc"), Play(3, "MPREd")]);

    private static async Task Load(Setup setup, params Shelf[] shelves)
    {
        var load = setup.Home.RefreshCommand.ExecuteAsync(null);
        setup.Catalog.HomeCalls[^1].CompletePage(new HomePage { Shelves = shelves });
        await load;
        await setup.Home.RecentLoad;
    }

    [Fact]
    public async Task YouTube_Musics_shelf_becomes_the_tiles_and_is_not_shown_twice()
    {
        var setup = new Setup { Log = () => Task.FromResult(FourAlbums()) };
        var revisit = Revisit();

        await Load(setup, Plain("Mixed for you"), revisit, Plain("New releases"));

        Assert.Equal(revisit.Entries, setup.Home.Tiles);
        Assert.Equal("Quick picks", setup.Home.TilesTitle);
        Assert.True(setup.Home.HasTiles);
        Assert.Equal(["Mixed for you", "New releases"], setup.Home.Shelves.Select(s => s.Title));
        Assert.DoesNotContain(revisit, setup.Home.Items);
        Assert.Equal(0, setup.Reads);
    }

    [Fact]
    public async Task Without_the_shelf_the_play_logs_albums_are_the_tiles()
    {
        var setup = new Setup { Log = () => Task.FromResult(FourAlbums()) };

        await Load(setup, Plain("Mixed for you"));

        Assert.Equal(["MPREd", "MPREc", "MPREb", "MPREa"], setup.Home.Tiles.Cast<AlbumEntry>().Select(a => a.BrowseId));
        Assert.Equal(Strings.Get("home.leading.recent"), setup.Home.TilesTitle);
        Assert.Equal(["Mixed for you"], setup.Home.Shelves.Select(s => s.Title));
    }

    [Fact]
    public async Task Signed_out_there_are_no_tiles_and_the_shelf_stays_a_shelf()
    {
        var setup = new Setup { SignedIn = false, Log = () => Task.FromResult(FourAlbums()) };

        await Load(setup, Revisit());

        Assert.Empty(setup.Home.Tiles);
        Assert.False(setup.Home.HasTiles);
        Assert.Equal(["Quick picks"], setup.Home.Shelves.Select(s => s.Title));
        Assert.Equal(0, setup.Reads);
    }

    [Fact]
    public async Task With_the_log_off_there_are_no_tiles()
    {
        var setup = new Setup { Log = null };

        await Load(setup, Plain("Mixed for you"));

        Assert.Equal(1, setup.Reads);
        Assert.Empty(setup.Home.Tiles);
    }

    [Fact]
    public async Task An_empty_newer_or_unreadable_log_gives_no_tiles_and_no_error()
    {
        foreach (var contents in new PlayLogContents[]
                 {
                     new(PlayLogState.Readable, []),
                     new(PlayLogState.Newer, []),
                     new(PlayLogState.Unreadable, []),
                 })
        {
            var setup = new Setup { Log = () => Task.FromResult(contents) };

            await Load(setup, Plain("Mixed for you"));

            Assert.Empty(setup.Home.Tiles);
            Assert.Null(setup.Home.LoadError);
        }
    }

    [Fact]
    public async Task A_failed_read_gives_no_tiles_and_no_error()
    {
        var setup = new Setup { Log = () => Task.FromException<PlayLogContents>(new IOException("locked")) };

        await Load(setup, Plain("Mixed for you"));

        Assert.Empty(setup.Home.Tiles);
        Assert.Null(setup.Home.LoadError);
        Assert.Equal(["Mixed for you"], setup.Home.Shelves.Select(s => s.Title));
    }

    [Fact]
    public async Task The_shelves_show_before_the_log_is_read()
    {
        var read = new TaskCompletionSource<PlayLogContents>();
        var setup = new Setup { Log = () => read.Task };

        var load = setup.Home.RefreshCommand.ExecuteAsync(null);
        setup.Catalog.HomeCalls[0].CompletePage(new HomePage { Shelves = [Plain("Mixed for you")] });

        Assert.Equal(["Mixed for you"], setup.Home.Shelves.Select(s => s.Title));
        Assert.False(setup.Home.IsLoading);
        Assert.Empty(setup.Home.Tiles);

        await load;
        read.SetResult(FourAlbums());
        await setup.Home.RecentLoad;
        Assert.Equal(4, setup.Home.Tiles.Count);
    }

    [Fact]
    public async Task Tiles_that_come_after_the_user_moved_on_are_left_out()
    {
        var read = new TaskCompletionSource<PlayLogContents>();
        var setup = new Setup { Log = () => read.Task };

        var load = setup.Home.RefreshCommand.ExecuteAsync(null);
        setup.Catalog.HomeCalls[0].CompletePage(new HomePage { Shelves = [Plain("Mixed for you")] });
        setup.AtRest = false;
        read.SetResult(FourAlbums());
        await load;
        await setup.Home.RecentLoad;

        Assert.Empty(setup.Home.Tiles);
    }

    [Fact]
    public async Task A_refresh_keeps_the_play_logs_tiles_until_the_log_is_read_again()
    {
        var setup = new Setup { Log = () => Task.FromResult(FourAlbums()) };
        await Load(setup, Plain("Mixed for you"));
        var before = setup.Home.Tiles;

        var read = new TaskCompletionSource<PlayLogContents>();
        setup.Log = () => read.Task;
        var load = setup.Home.RefreshCommand.ExecuteAsync(null);
        setup.Catalog.HomeCalls[1].CompletePage(new HomePage { Shelves = [Plain("Fresh")] });

        Assert.Same(before, setup.Home.Tiles);
        read.SetResult(new PlayLogContents(PlayLogState.Readable, [.. FourAlbums().Plays, Play(9, "MPREe")]));
        await load;
        await setup.Home.RecentLoad;
        Assert.Equal("MPREe", ((AlbumEntry)setup.Home.Tiles[0]).BrowseId);
    }

    [Fact]
    public async Task A_mood_chip_shows_no_tiles()
    {
        var setup = new Setup { Log = () => Task.FromResult(FourAlbums()) };
        await Load(setup, Revisit());
        Assert.True(setup.Home.HasTiles);

        var chip = new HomeChip("Relax", "RELAX", IsSelected: false);
        var select = setup.Home.SelectChipCommand.ExecuteAsync(chip);
        setup.Catalog.HomeCalls[^1].CompletePage(new HomePage { Shelves = [Revisit("Quick picks")] });
        await select;

        Assert.False(setup.Home.HasTiles);
        Assert.Equal(["Quick picks"], setup.Home.Shelves.Select(s => s.Title));
    }

    [Fact]
    public async Task A_read_overtaken_by_a_refresh_is_dropped()
    {
        var stale = new TaskCompletionSource<PlayLogContents>();
        var setup = new Setup { Log = () => stale.Task };
        var first = setup.Home.RefreshCommand.ExecuteAsync(null);
        setup.Catalog.HomeCalls[0].CompletePage(new HomePage { Shelves = [Plain("Mixed for you")] });
        await first;
        var staleRead = setup.Home.RecentLoad;

        var second = setup.Home.RefreshCommand.ExecuteAsync(null);
        setup.Catalog.HomeCalls[1].CompletePage(new HomePage { Shelves = [Revisit()] });
        await second;
        stale.SetResult(FourAlbums());
        await staleRead;

        Assert.Equal("Quick picks", setup.Home.TilesTitle);
    }
}
