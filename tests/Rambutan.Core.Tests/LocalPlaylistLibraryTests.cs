using Rambutan.Core.Models;
using Rambutan.Core.Playlists;

namespace Rambutan.Core.Tests;

public class LocalPlaylistLibraryTests
{
    private sealed class FakeStore(LoadedLocalPlaylists? loaded = null) : ILocalPlaylistStore
    {
        public List<IReadOnlyList<LocalPlaylist>> Saves { get; } = [];
        public bool Fail { get; set; }
        public int Loads { get; private set; }

        public LoadedLocalPlaylists Load()
        {
            Loads++;
            return loaded ?? new([]);
        }

        public void Save(IReadOnlyList<LocalPlaylist> playlists)
        {
            if (Fail)
                throw new IOException("disk full");
            Saves.Add(playlists.ToArray());
        }
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static TrackInfo Song(string id) => new() { VideoId = id, Title = id, Artists = [] };

    private static (LocalPlaylistLibrary Library, FakeStore Store) Make(LoadedLocalPlaylists? loaded = null)
    {
        var store = new FakeStore(loaded);
        var clock = Start;
        var ids = 0;
        var library = new LocalPlaylistLibrary(store, () => clock = clock.AddMinutes(1), () => $"id{++ids}");
        return (library, store);
    }

    [Fact]
    public void Create_saves_and_raises_Changed()
    {
        var (library, store) = Make();
        var raised = 0;
        library.Changed += () => raised++;

        var created = library.Create("  Mix ", [LocalPlaylistItem.Of(Song("a")), LocalPlaylistItem.Of(Song("a"))]);

        Assert.Equal("Mix", created.Name);
        Assert.Equal(["a"], created.Items.Select(i => i.Track.VideoId));
        Assert.Single(store.Saves);
        Assert.Equal(1, raised);
        Assert.Same(created, library.Find(created.Id));
    }

    [Fact]
    public void A_blank_name_is_refused_and_nothing_is_saved()
    {
        var (library, store) = Make();

        Assert.Throws<ArgumentException>(() => library.Create("  "));
        Assert.Empty(store.Saves);
        Assert.Empty(library.All);
    }

    [Fact]
    public void All_lists_the_one_changed_last_first()
    {
        var (library, _) = Make();
        var first = library.Create("First");
        library.Create("Second");

        library.Add(first.Id, [LocalPlaylistItem.Of(Song("a"))]);

        Assert.Equal(["First", "Second"], library.All.Select(p => p.Name));
    }

    [Fact]
    public void Add_says_how_many_were_added_and_saves_only_a_change()
    {
        var (library, store) = Make();
        var playlist = library.Create("Mix", [LocalPlaylistItem.Of(Song("a"))]);
        var raised = 0;
        library.Changed += () => raised++;

        Assert.Equal(1, library.Add(playlist.Id, [LocalPlaylistItem.Of(Song("a")), LocalPlaylistItem.Of(Song("b"))]));
        Assert.Equal(0, library.Add(playlist.Id, [LocalPlaylistItem.Of(Song("b"))]));
        Assert.Null(library.Add("nope", [LocalPlaylistItem.Of(Song("c"))]));

        Assert.Equal(2, store.Saves.Count);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Remove_move_rename_and_delete_change_the_playlist()
    {
        var (library, store) = Make();
        var id = library.Create("Mix", [LocalPlaylistItem.Of(Song("a")), LocalPlaylistItem.Of(Song("b")), LocalPlaylistItem.Of(Song("c"))]).Id;

        Assert.True(library.Move(id, 0, 2));
        Assert.True(library.RemoveAt(id, 0));
        Assert.True(library.Rename(id, "Renamed"));
        Assert.Equal(["c", "a"], library.Find(id)!.Items.Select(i => i.Track.VideoId));
        Assert.Equal("Renamed", library.Find(id)!.Name);

        Assert.True(library.Delete(id));
        Assert.False(library.Delete(id));
        Assert.Null(library.Find(id));
        Assert.Empty(store.Saves[^1]);
        Assert.Equal(5, store.Saves.Count);
    }

    [Fact]
    public void Renaming_to_the_same_name_saves_nothing()
    {
        var (library, store) = Make();
        var id = library.Create("Mix").Id;

        Assert.True(library.Rename(id, " Mix "));
        Assert.Single(store.Saves);
    }

    [Fact]
    public void A_failed_save_undoes_the_change_and_throws()
    {
        var (library, store) = Make();
        var id = library.Create("Mix").Id;
        var raised = 0;
        library.Changed += () => raised++;
        store.Fail = true;

        Assert.Throws<IOException>(() => library.Add(id, [LocalPlaylistItem.Of(Song("a"))]));
        Assert.Throws<IOException>(() => library.Delete(id));
        Assert.Throws<IOException>(() => library.Create("Other"));

        Assert.Empty(library.Find(id)!.Items);
        Assert.Single(library.All);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Copy_keeps_duplicates()
    {
        var (library, _) = Make();

        var copy = library.Copy("Road trip", [Song("a"), Song("a")], "PLroad");

        Assert.Equal(2, copy.Items.Count);
        Assert.Equal("PLroad", library.Find(copy.Id)!.CopiedFrom);
    }

    [Fact]
    public void A_newer_builds_file_is_read_only()
    {
        var (library, store) = Make(new([], LocalPlaylistsProblem.Newer));

        Assert.Equal(LocalPlaylistsProblem.Newer, library.Problem);
        Assert.Throws<LocalPlaylistsReadOnlyException>(() => library.Create("Mix"));
        Assert.Empty(store.Saves);
    }

    [Fact]
    public void An_unreadable_file_says_where_it_was_set_aside()
    {
        var (library, _) = Make(new([], LocalPlaylistsProblem.Unreadable, "/d/local-playlists.json.unreadable"));

        Assert.Equal(LocalPlaylistsProblem.Unreadable, library.Problem);
        Assert.Equal("/d/local-playlists.json.unreadable", library.SetAside);
        Assert.NotNull(library.Create("Mix"));
    }

    [Fact]
    public void An_unreadable_file_still_in_its_place_is_read_only()
    {
        var (library, store) = Make(new([], LocalPlaylistsProblem.Unreadable, SetAside: null));

        Assert.Throws<LocalPlaylistsReadOnlyException>(() => library.Create("Mix"));
        Assert.Empty(store.Saves);
    }

    [Fact]
    public void The_store_is_read_once()
    {
        var (library, store) = Make();

        _ = library.All;
        _ = library.Problem;
        library.Create("Mix");

        Assert.Equal(1, store.Loads);
    }

    [Fact]
    public void KeepResolved_saves_found_again_rows_without_raising_Changed()
    {
        var (library, store) = Make();
        var playlist = library.Create("Mix", [LocalPlaylistItem.Of(Song("a"))]);
        var raised = 0;
        library.Changed += () => raised++;
        var resolved = playlist with { Items = [LocalPlaylistItem.Of(Song("a")) with { Path = "/n/a.flac" }] };

        library.KeepResolved(resolved);

        Assert.Equal("/n/a.flac", library.Find(playlist.Id)!.Items[0].Path);
        Assert.Equal(2, store.Saves.Count);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void KeepResolved_drops_rows_read_before_a_change_of_the_users()
    {
        var (library, store) = Make();
        var playlist = library.Create("Mix", [LocalPlaylistItem.Of(Song("a"))]);
        var stale = playlist with { Items = [LocalPlaylistItem.Of(Song("a")) with { Path = "/n/a.flac" }] };
        library.Rename(playlist.Id, "Renamed");

        library.KeepResolved(stale);

        Assert.Equal("Renamed", library.Find(playlist.Id)!.Name);
        Assert.Null(library.Find(playlist.Id)!.Items[0].Path);
        Assert.Equal(2, store.Saves.Count);
    }

    [Fact]
    public void KeepResolved_whose_save_fails_keeps_the_rows_as_they_were()
    {
        var (library, store) = Make();
        var playlist = library.Create("Mix", [LocalPlaylistItem.Of(Song("a"))]);
        store.Fail = true;

        library.KeepResolved(playlist with { Items = [LocalPlaylistItem.Of(Song("a")) with { Path = "/n/a.flac" }] });

        Assert.Null(library.Find(playlist.Id)!.Items[0].Path);
    }
}
