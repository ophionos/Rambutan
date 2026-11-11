using Rambutan.Core.Sidebar;

namespace Rambutan.Core.Tests;

public class SidebarPinLibraryTests
{
    private sealed class MemoryStore(LoadedSidebarPins? loaded = null) : ISidebarPinStore
    {
        public IReadOnlyList<SidebarPin>? Saved { get; private set; }
        public int Saves { get; private set; }
        public bool FailSaves { get; set; }

        public LoadedSidebarPins Load() => loaded ?? new([]);

        public void Save(IReadOnlyList<SidebarPin> pins)
        {
            if (FailSaves)
                throw new IOException("disk full");
            Saved = [.. pins];
            Saves++;
        }
    }

    private static readonly SidebarPin Album = new(PinKind.Album, "MPREb_a", "Discovery", "https://example.test/a.jpg");
    private static readonly SidebarPin Playlist = new(PinKind.Playlist, "PL1", "Late Night Coding");
    private static readonly SidebarPin Local = new(PinKind.LocalPlaylist, "0f3a", "On this computer");

    [Fact]
    public void Pin_AddsAtTheEnd_Saves_AndTellsListeners()
    {
        var store = new MemoryStore();
        var library = new SidebarPinLibrary(store);
        var changes = 0;
        library.Changed += () => changes++;

        Assert.True(library.Pin(Album));
        Assert.True(library.Pin(Playlist));

        Assert.Equal([Album, Playlist], library.Pins);
        Assert.Equal([Album, Playlist], store.Saved);
        Assert.Equal(2, changes);
        Assert.True(library.IsPinned(PinKind.Album, "MPREb_a"));
        Assert.False(library.IsPinned(PinKind.Playlist, "MPREb_a"));
    }

    [Fact]
    public void Pin_Twice_OrWithoutIdOrTitle_DoesNothing()
    {
        var store = new MemoryStore();
        var library = new SidebarPinLibrary(store);
        library.Pin(Album);

        Assert.False(library.Pin(Album with { Title = "Another title" }));
        Assert.False(library.Pin(new SidebarPin(PinKind.Playlist, "", "No id")));
        Assert.False(library.Pin(new SidebarPin(PinKind.Playlist, "PL2", "  ")));

        Assert.Equal([Album], library.Pins);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void TheSameId_OfAnotherKind_IsAnotherPin()
    {
        var library = new SidebarPinLibrary(new MemoryStore());

        Assert.True(library.Pin(new SidebarPin(PinKind.Playlist, "x", "A playlist")));
        Assert.True(library.Pin(new SidebarPin(PinKind.LocalPlaylist, "x", "A local playlist")));

        Assert.Equal(2, library.Pins.Count);
    }

    [Fact]
    public void Unpin_RemovesOnlyThatPin()
    {
        var store = new MemoryStore(new([Album, Playlist, Local]));
        var library = new SidebarPinLibrary(store);

        Assert.True(library.Unpin(PinKind.Playlist, "PL1"));
        Assert.False(library.Unpin(PinKind.Playlist, "PL1"));

        Assert.Equal([Album, Local], library.Pins);
        Assert.Equal([Album, Local], store.Saved);
    }

    [Fact]
    public void Move_GoesUpAndDown_AndStopsAtTheEnds()
    {
        var library = new SidebarPinLibrary(new MemoryStore(new([Album, Playlist, Local])));

        Assert.True(library.Move(PinKind.LocalPlaylist, "0f3a", -1));
        Assert.Equal([Album, Local, Playlist], library.Pins);

        Assert.True(library.Move(PinKind.Album, "MPREb_a", 1));
        Assert.Equal([Local, Album, Playlist], library.Pins);

        Assert.False(library.Move(PinKind.LocalPlaylist, "0f3a", -1));
        Assert.False(library.Move(PinKind.Playlist, "PL1", 1));
        Assert.False(library.Move(PinKind.Playlist, "gone", 1));
        Assert.Equal([Local, Album, Playlist], library.Pins);

        Assert.False(library.CanMove(PinKind.LocalPlaylist, "0f3a", -1));
        Assert.True(library.CanMove(PinKind.LocalPlaylist, "0f3a", 1));
        Assert.False(library.CanMove(PinKind.Playlist, "PL1", 1));
    }

    [Fact]
    public void Refresh_TakesANewTitleAndCover_AndKeepsTheOldOnesWhenThereAreNone()
    {
        var store = new MemoryStore(new([Album]));
        var library = new SidebarPinLibrary(store);

        Assert.True(library.Refresh(PinKind.Album, "MPREb_a", "Discovery (Remaster)", null));
        Assert.Equal(Album with { Title = "Discovery (Remaster)" }, library.Pins[0]);

        Assert.True(library.Refresh(PinKind.Album, "MPREb_a", null, "https://example.test/b.jpg"));
        Assert.Equal("Discovery (Remaster)", library.Pins[0].Title);
        Assert.Equal("https://example.test/b.jpg", library.Pins[0].CoverUrl);

        // Nothing new: no save, no change.
        var saves = store.Saves;
        Assert.False(library.Refresh(PinKind.Album, "MPREb_a", "Discovery (Remaster)", "https://example.test/b.jpg"));
        Assert.False(library.Refresh(PinKind.Album, "MPREb_a", "", ""));
        Assert.False(library.Refresh(PinKind.Album, "not pinned", "Title", null));
        Assert.Equal(saves, store.Saves);
    }

    [Fact]
    public void AReadOnlyFile_GivesNoPins_AndRefusesEveryChange()
    {
        var store = new MemoryStore(new([], ReadOnly: true));
        var library = new SidebarPinLibrary(store);
        var changes = 0;
        library.Changed += () => changes++;

        Assert.True(library.IsReadOnly);
        Assert.False(library.Pin(Album));
        Assert.False(library.Unpin(PinKind.Album, "MPREb_a"));
        Assert.False(library.Move(PinKind.Album, "MPREb_a", 1));
        Assert.False(library.Refresh(PinKind.Album, "MPREb_a", "Title", null));

        Assert.Empty(library.Pins);
        Assert.Equal(0, store.Saves);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void AFailedSave_KeepsTheChangeForThisRun_AndIsLogged()
    {
        var store = new MemoryStore { FailSaves = true };
        var log = new List<string>();
        var library = new SidebarPinLibrary(store, log.Add);

        Assert.True(library.Pin(Album));

        Assert.Equal([Album], library.Pins);
        Assert.Contains("disk full", Assert.Single(log));
    }

    [Fact]
    public void Loading_DropsRepeats_AndEntriesThatCantOpen()
    {
        var library = new SidebarPinLibrary(new MemoryStore(new(
        [
            Album,
            Album with { Title = "The same album again" },
            new SidebarPin(PinKind.Playlist, "", "No id"),
            new SidebarPin((PinKind)42, "x", "A kind this build doesn't know"),
            Playlist,
        ])));

        Assert.Equal([Album, Playlist], library.Pins);
    }
}
