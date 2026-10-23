using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.Core.Playlists;

namespace Rambutan.App.Tests;

/// <summary>Add to playlist offers the playlists a song can be added to: the
/// user's own on YouTube Music, from every page of the library, and those on
/// this computer.</summary>
public class PlaylistPickerTests
{
    private sealed class MemoryStore(LoadedLocalPlaylists? loaded = null) : ILocalPlaylistStore
    {
        public bool Fail { get; set; }

        public LoadedLocalPlaylists Load() => loaded ?? new([]);

        public void Save(IReadOnlyList<LocalPlaylist> playlists)
        {
            if (Fail)
                throw new IOException("disk full");
        }
    }

    private readonly FakeCatalog _catalog = new();
    private readonly ToastHostViewModel _toasts = new(new ManualClock().Schedule);
    private PlaylistPickOptions? _offered;
    private PlaylistPickResult? _pick;

    private TrackActionsViewModel Actions(
        LocalPlaylistLibrary? local = null, bool signedIn = true, Func<string, LocalFile?>? find = null) =>
        new(_catalog, null!, null!, new TrackRatings(_catalog), _toasts)
        {
            PlaylistPicker = options =>
            {
                _offered = options;
                return Task.FromResult(_pick);
            },
            LocalPlaylists = local,
            FindLocalFile = find,
            IsSignedIn = () => signedIn,
        };

    private static LocalPlaylistLibrary Library(LoadedLocalPlaylists? loaded = null) => new(new MemoryStore(loaded));

    private static PlaylistEntry Playlist(string id, bool? isOwn) =>
        new() { Title = id, PlaylistId = id, IsOwn = isOwn };

    private static TrackInfo Song(string id) => new() { VideoId = id, Title = id.ToUpperInvariant(), Artists = [] };

    private string LastToast => _toasts.Toasts[^1].Message;

    [Fact]
    public async Task OffersOwnPlaylists_NotSavedOnesOrLikedMusic()
    {
        var pick = Actions().AddToPlaylistCommand.ExecuteAsync(Song("v"));

        _catalog.LibraryCalls[0].Result.SetResult(new LibraryPage
        {
            Items = [Playlist("LM", null), Playlist("PLown1", true), Playlist("PLsaved", false)],
            Continuation = new ContinuationToken("next"),
        });
        // The first page's result runs the command on to the next request.
        _catalog.LibraryCalls[1].Result.SetResult(new LibraryPage
        {
            Items = [Playlist("RDCLAKsaved", false), Playlist("PLown2", true), Playlist("PLunknown", null)],
        });
        await pick;

        // A card that doesn't say whose it is stays on offer.
        Assert.Equal(["PLown1", "PLown2", "PLunknown"], _offered!.YouTube.Select(p => p.PlaylistId));
        Assert.True(_offered.OffersYouTube);
        Assert.True(_offered.CanCreateOnYouTube);
        Assert.False(_offered.OffersLocal);
    }

    [Fact]
    public async Task ASong_IsOfferedBothGroups()
    {
        var local = Library();
        local.Create("Road trip");
        var pick = Actions(local).AddToPlaylistCommand.ExecuteAsync(Song("v"));

        _catalog.LibraryCalls[0].Result.SetResult(new LibraryPage { Items = [Playlist("PLown", true)] });
        await pick;

        Assert.Equal(["PLown"], _offered!.YouTube.Select(p => p.PlaylistId));
        Assert.Null(_offered.YouTubeNote);
        Assert.Equal(["Road trip"], _offered.Local.Select(p => p.Name));
        Assert.Null(_offered.LocalNote);
    }

    [Fact]
    public async Task ALocalFile_IsOfferedOnlyTheLocalGroup_AndNothingAsksYouTubeMusic()
    {
        await Actions(Library()).AddToPlaylistCommand.ExecuteAsync(Song("local:1"));

        Assert.False(_offered!.OffersYouTube);
        Assert.True(_offered.OffersLocal);
        Assert.Equal("No playlists on this computer yet.", _offered.LocalNote);
        Assert.Empty(_catalog.LibraryCalls);
    }

    [Fact]
    public async Task SignedOut_TheYouTubeMusicGroupSaysSo_AndTheLocalGroupStays()
    {
        await Actions(Library(), signedIn: false).AddToPlaylistCommand.ExecuteAsync(Song("v"));

        Assert.True(_offered!.OffersYouTube);
        Assert.False(_offered.CanCreateOnYouTube);
        Assert.Equal("Sign in to save to your YouTube Music playlists.", _offered.YouTubeNote);
        Assert.True(_offered.OffersLocal);
        Assert.Empty(_catalog.LibraryCalls);
    }

    [Fact]
    public async Task AFailedLibraryLoad_IsTheGroupsNote_AndTheLocalGroupStays()
    {
        var pick = Actions(Library()).AddToPlaylistCommand.ExecuteAsync(Song("v"));

        _catalog.LibraryCalls[0].Fail("offline");
        await pick;

        Assert.Equal("Couldn't load your YouTube Music playlists: offline", _offered!.YouTubeNote);
        Assert.False(_offered.CanCreateOnYouTube);
        Assert.True(_offered.OffersLocal);
        Assert.Empty(_toasts.Toasts);
    }

    [Fact]
    public async Task WithoutLocalPlaylists_AFailedLoad_IsAToast_AsBefore()
    {
        var pick = Actions().AddToPlaylistCommand.ExecuteAsync(Song("v"));

        _catalog.LibraryCalls[0].Fail("offline");
        await pick;

        Assert.Null(_offered);
        Assert.Equal("Couldn't add to playlist: offline", LastToast);
    }

    [Fact]
    public async Task ANewLocalPlaylist_IsCreatedWithTheSong()
    {
        var local = Library();
        _pick = new PlaylistPickResult(null, null, NewLocalName: "Mix");

        await Actions(local).AddToPlaylistCommand.ExecuteAsync(Song("local:1"));

        var created = Assert.Single(local.All);
        Assert.Equal("Mix", created.Name);
        Assert.Equal(["local:1"], created.Items.Select(i => i.Track.VideoId));
        Assert.Equal("Added “LOCAL:1” to Mix", LastToast);
    }

    [Fact]
    public async Task ASongAlreadyThere_IsSkipped_AndTheToastSaysSo()
    {
        var local = Library();
        var playlist = local.Create("Mix", [LocalPlaylistItem.Of(Song("local:1"))]);
        _pick = new PlaylistPickResult(null, null, ExistingLocal: playlist);

        await Actions(local).AddToPlaylistCommand.ExecuteAsync(Song("local:1"));

        Assert.Single(local.Find(playlist.Id)!.Items);
        Assert.Equal("“LOCAL:1” is already in Mix", LastToast);
    }

    [Fact]
    public async Task ALocalFile_KeepsItsOwnTags_NotWhatItsMatchFilledIn()
    {
        var local = Library();
        var playlist = local.Create("Mix");
        var file = new LocalFile("local:1", "/m/a.flac", 1, DateTimeOffset.UnixEpoch,
            new LocalFileTags("flac", TimeSpan.FromSeconds(100), "Song", "Tag Artist", "Tag Album"));
        var shown = new TrackInfo
        {
            VideoId = "local:1", Title = "Song", Artists = [new ArtistRef("Matched Artist", "UCm")], Album = "Matched Album",
        };
        _pick = new PlaylistPickResult(null, null, ExistingLocal: playlist);

        await Actions(local, find: id => id == file.Id ? file : null).AddToPlaylistCommand.ExecuteAsync(shown);

        var item = Assert.Single(local.Find(playlist.Id)!.Items);
        Assert.Equal("Tag Artist", item.Track.Artists[0].Name);
        Assert.Equal("Tag Album", item.Track.Album);
        Assert.Equal("/m/a.flac", item.Path);
    }

    [Fact]
    public async Task ReadOnlyPlaylists_AreOfferedDisabled_WithTheirReason()
    {
        var local = Library(new([], LocalPlaylistsProblem.Newer));

        await Actions(local).AddToPlaylistCommand.ExecuteAsync(Song("local:1"));

        Assert.True(_offered!.LocalReadOnly);
        Assert.Equal(
            "Your playlists on this computer were saved by a newer version of Rambutan, so they can't be changed here.",
            _offered.LocalNote);
    }

    [Fact]
    public async Task AFailedLocalSave_IsAnErrorToast()
    {
        var store = new MemoryStore();
        var local = new LocalPlaylistLibrary(store);
        var playlist = local.Create("Mix");
        store.Fail = true;
        _pick = new PlaylistPickResult(null, null, ExistingLocal: playlist);

        await Actions(local).AddToPlaylistCommand.ExecuteAsync(Song("local:1"));

        Assert.Equal("Couldn't add to playlist: disk full", LastToast);
        Assert.True(_toasts.Toasts[^1].IsError);
        Assert.Empty(local.Find(playlist.Id)!.Items);
    }

    [Fact]
    public void ALocalFile_CanBeSaved_OnlyWhereThereAreLocalPlaylists()
    {
        Assert.True(Actions(Library()).AddToPlaylistCommand.CanExecute(Song("local:1")));
        Assert.False(Actions().AddToPlaylistCommand.CanExecute(Song("local:1")));
    }
}
