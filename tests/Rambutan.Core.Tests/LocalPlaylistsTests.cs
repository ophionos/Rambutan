using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.Core.Playlists;

namespace Rambutan.Core.Tests;

public class LocalPlaylistsTests
{
    private static readonly DateTimeOffset Then = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Then.AddDays(1);
    private static readonly LocalPaths Unix = new(ignoreCase: false, '/');

    private static TrackInfo Song(string id) => new() { VideoId = id, Title = id, Artists = [new ArtistRef("A", null)] };

    private static LocalFile File(string id, string path, string? title = "Song", string? artist = "Artist",
        string? album = "Album", double seconds = 200, bool missing = false) =>
        new(id, path, 1, Then, new LocalFileTags("flac", TimeSpan.FromSeconds(seconds), title, artist, album), IsMissing: missing);

    private static LocalPlaylist Playlist(params LocalPlaylistItem[] items) => new("p", "Mine", items, Then, Then);

    private static Func<string, LocalFile?> Index(params LocalFile[] files) =>
        id => files.FirstOrDefault(f => f.Id == id);

    [Fact]
    public void Add_appends_in_order_and_skips_what_it_holds_or_is_given_twice()
    {
        var playlist = Playlist(LocalPlaylistItem.Of(Song("a")));

        var (next, added) = LocalPlaylists.Add(playlist,
            [LocalPlaylistItem.Of(Song("b")), LocalPlaylistItem.Of(Song("a")), LocalPlaylistItem.Of(Song("b")), LocalPlaylistItem.Of(Song("c"))], Now);

        Assert.Equal(2, added);
        Assert.Equal(["a", "b", "c"], next.Items.Select(i => i.Track.VideoId));
        Assert.Equal(Now, next.ChangedAt);
    }

    [Fact]
    public void Adding_only_what_it_holds_changes_nothing()
    {
        var playlist = Playlist(LocalPlaylistItem.Of(Song("a")));

        var (next, added) = LocalPlaylists.Add(playlist, [LocalPlaylistItem.Of(Song("a"))], Now);

        Assert.Equal(0, added);
        Assert.Same(playlist, next);
    }

    [Fact]
    public void RemoveAt_removes_that_row_only_even_when_the_song_is_there_twice()
    {
        var playlist = LocalPlaylists.Copy("p", "Copy", [Song("a"), Song("b"), Song("a")], "PLx", Then);

        var next = LocalPlaylists.RemoveAt(playlist, 2, Now);

        Assert.Equal(["a", "b"], next.Items.Select(i => i.Track.VideoId));
        Assert.Equal(Now, next.ChangedAt);
        Assert.Same(playlist, LocalPlaylists.RemoveAt(playlist, 3, Now));
        Assert.Same(playlist, LocalPlaylists.RemoveAt(playlist, -1, Now));
    }

    [Theory]
    [InlineData(1, "axb")]
    [InlineData(0, "xab")]
    [InlineData(9, "abx")]
    [InlineData(-1, "xab")]
    public void Insert_puts_the_row_back_at_its_place(int index, string expected)
    {
        var playlist = Playlist(LocalPlaylistItem.Of(Song("a")), LocalPlaylistItem.Of(Song("b")));

        var next = LocalPlaylists.Insert(playlist, index, LocalPlaylistItem.Of(Song("x")), Now);

        Assert.Equal(expected, string.Concat(next.Items.Select(i => i.Track.VideoId)));
        Assert.Equal(Now, next.ChangedAt);
    }

    [Fact]
    public void Insert_puts_back_a_song_the_playlist_still_holds()
    {
        var playlist = Playlist(LocalPlaylistItem.Of(Song("a")));

        var next = LocalPlaylists.Insert(playlist, 0, LocalPlaylistItem.Of(Song("a")), Now);

        Assert.Equal(["a", "a"], next.Items.Select(i => i.Track.VideoId));
    }

    [Theory]
    [InlineData(0, 2, "bca")]
    [InlineData(2, 0, "cab")]
    [InlineData(1, 1, "abc")]
    [InlineData(0, 3, "abc")]
    [InlineData(-1, 0, "abc")]
    public void Move_puts_the_row_at_its_new_place(int from, int to, string expected)
    {
        var playlist = Playlist(LocalPlaylistItem.Of(Song("a")), LocalPlaylistItem.Of(Song("b")), LocalPlaylistItem.Of(Song("c")));

        var next = LocalPlaylists.Move(playlist, from, to, Now);

        Assert.Equal(expected, string.Concat(next.Items.Select(i => i.Track.VideoId)));
        Assert.Equal(expected == "abc" ? Then : Now, next.ChangedAt);
    }

    [Fact]
    public void A_copy_keeps_the_duplicates_and_where_it_came_from()
    {
        var copy = LocalPlaylists.Copy("p", "Road trip", [Song("a"), Song("a")], "PLroad", Now);

        Assert.Equal(["a", "a"], copy.Items.Select(i => i.Track.VideoId));
        Assert.Equal("PLroad", copy.CopiedFrom);
        Assert.Equal(Now, copy.CreatedAt);
        Assert.All(copy.Items, i => Assert.Null(i.Path));
    }

    [Theory]
    [InlineData("  Mix  ", "Mix")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CleanName_trims_and_refuses_a_blank_name(string? name, string? expected) =>
        Assert.Equal(expected, LocalPlaylists.CleanName(name));

    [Fact]
    public void A_youtube_song_resolves_as_it_is()
    {
        var item = LocalPlaylistItem.Of(Song("a"));

        var row = LocalPlaylists.Resolve(item, Index(), [], Unix);

        Assert.Same(item, row.Item);
        Assert.False(row.IsMissing);
    }

    [Fact]
    public void A_present_file_resolves_by_its_id_with_its_tags_now()
    {
        var item = LocalPlaylistItem.Of(File("local:1", "/m/a.flac", title: "Old"));
        var now = File("local:1", "/m/a.flac", title: "New");

        var row = LocalPlaylists.Resolve(item, Index(now), [now], Unix);

        Assert.False(row.IsMissing);
        Assert.Equal("New", row.Item.Track.Title);
    }

    [Fact]
    public void A_file_gone_from_its_id_is_found_at_its_path()
    {
        var item = LocalPlaylistItem.Of(File("local:1", "/m/a.flac"));
        var again = File("local:7", "/m/a.flac", title: "Other");

        var row = LocalPlaylists.Resolve(item, Index(File("local:1", "/m/a.flac", missing: true), again), [again], Unix);

        Assert.False(row.IsMissing);
        Assert.Equal("local:7", row.Item.Track.VideoId);
    }

    [Fact]
    public void A_moved_file_is_found_again_by_its_tags()
    {
        var item = LocalPlaylistItem.Of(File("local:1", "/m/a.flac", seconds: 200));
        var moved = File("local:9", "/n/renamed.flac", title: "SONG ", artist: "artist", album: "album", seconds: 200.8);
        var other = File("local:10", "/n/other.flac", title: "Song", artist: "Artist", album: "Album", seconds: 260);

        var row = LocalPlaylists.Resolve(item, Index(), [other, moved], Unix);

        Assert.False(row.IsMissing);
        Assert.Equal("local:9", row.Item.Track.VideoId);
        Assert.Equal("/n/renamed.flac", row.Item.Path);
    }

    [Fact]
    public void Two_files_with_its_tags_find_neither()
    {
        var item = LocalPlaylistItem.Of(File("local:1", "/m/a.flac"));

        var row = LocalPlaylists.Resolve(item, Index(),
            [File("local:8", "/n/one.flac"), File("local:9", "/n/two.flac")], Unix);

        Assert.True(row.IsMissing);
        Assert.Same(item, row.Item);
    }

    [Fact]
    public void A_file_without_a_title_tag_isnt_found_by_its_name()
    {
        // ToTrack titles an untagged file after its name, which isn't a tag.
        var item = LocalPlaylistItem.Of(File("local:1", "/m/Song.flac", title: null, artist: null, album: null));
        var lookalike = File("local:9", "/n/Song.flac", title: null, artist: null, album: null);

        var row = LocalPlaylists.Resolve(item, Index(), [lookalike], Unix);

        Assert.True(row.IsMissing);
    }

    [Fact]
    public void An_unreadable_file_under_its_id_isnt_it()
    {
        var item = LocalPlaylistItem.Of(File("local:1", "/m/a.flac"));
        var unreadable = new LocalFile("local:1", "/m/a.flac", 1, Then, Tags: null);

        var row = LocalPlaylists.Resolve(item, Index(unreadable), [], Unix);

        Assert.True(row.IsMissing);
    }

    [Fact]
    public void ResolveAll_gives_a_playlist_to_keep_only_when_a_row_changed()
    {
        var file = File("local:1", "/m/a.flac");
        var playlist = Playlist(LocalPlaylistItem.Of(Song("y")), LocalPlaylistItem.Of(file));

        var (rows, unchanged) = LocalPlaylists.ResolveAll(playlist, Index(file), [file], Unix);
        Assert.Equal(2, rows.Count);
        Assert.Null(unchanged);

        var moved = File("local:2", "/n/a.flac");
        var (_, changed) = LocalPlaylists.ResolveAll(playlist, Index(file with { IsMissing = true }, moved), [moved], Unix);
        Assert.NotNull(changed);
        Assert.Equal("local:2", changed.Items[1].Track.VideoId);
        Assert.Equal(Then, changed.ChangedAt);
    }

    [Fact]
    public void ResolveAll_leaves_a_missing_row_as_it_was()
    {
        var file = File("local:1", "/m/a.flac");
        var playlist = Playlist(LocalPlaylistItem.Of(file));

        var (rows, changed) = LocalPlaylists.ResolveAll(playlist, Index(), [], Unix);

        Assert.True(rows[0].IsMissing);
        Assert.Null(changed);
    }
}
