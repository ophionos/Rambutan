using System.Net.Http;
using Rambutan.App.Services.Demo;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;
using Rambutan.InnerTube;

namespace Rambutan.App.Tests;

/// <summary>
/// Demo mode runs the real InnerTube client and parsers over the recorded
/// fixtures, so these go end to end: request → demo handler → fixture → parser.
/// </summary>
public class DemoModeTests
{
    private static readonly string Fixtures = FindFixtures();

    private static DemoOptions Options(bool signedIn = true, params string[] failing) =>
        new(signedIn, Fixtures, TimeSpan.Zero, failing.ToHashSet());

    private static InnerTubeClient Client(DemoOptions options)
    {
        var client = new InnerTubeClient(new HttpClient(new DemoInnerTubeHandler(options)));
        if (options.SignedIn)
            client.SetSession(InnerTubeSession.FromCookieString("SAPISID=demo"));
        return client;
    }

    [Fact]
    public async Task CookieRotation_SucceedsAndChangesNothing()
    {
        // Session restore rotates cookies; its non-JSON body used to throw here
        // and surface as "Couldn't restore your session".
        var client = Client(Options());
        var before = client.Session;

        Assert.True(await client.RotateCookiesAsync());
        Assert.Same(before, client.Session);
    }

    [Fact]
    public async Task Home_ComesFromTheRecording()
    {
        var shelves = (await Client(Options()).GetHomeAsync()).Shelves;
        Assert.NotEmpty(shelves);
        Assert.All(shelves, s => Assert.NotEmpty(s.Entries));
    }

    [Fact]
    public async Task Home_PagesThroughItsRecordings_ThenEnds()
    {
        var client = Client(Options());

        var first = await client.GetHomeAsync();
        var middle = await client.GetHomeContinuationAsync(first.Continuation!);
        var last = await client.GetHomeContinuationAsync(middle.Continuation!);

        Assert.Equal(["Quick picks", "New releases"], middle.Shelves.Select(s => s.Title));
        Assert.Equal("Featured playlists for you", Assert.Single(last.Shelves).Title);
        Assert.Null(last.Continuation);
    }

    [Fact]
    public async Task AMoodChip_OpensTheFilteredRecording_WhichPagesOn()
    {
        var client = Client(Options());
        var chip = (await client.GetHomeAsync()).Chips[2];

        var filtered = await client.GetHomeAsync(chip);
        var more = await client.GetHomeContinuationAsync(filtered.Continuation!);

        Assert.Equal("Relax", Assert.Single(filtered.Chips, c => c.IsSelected).Title);
        Assert.NotEmpty(more.Shelves);
    }

    [Fact]
    public async Task MoodsAndGenres_OpenTheirRecordings()
    {
        var client = Client(Options());

        var grid = await client.GetMoodsAndGenresAsync();
        var genres = grid.Sections[1].Categories;
        var soundtracks = await client.GetMoodCategoryAsync(genres.Single(c => c.Title == "Soundtracks & musicals"));
        var chill = await client.GetMoodCategoryAsync(genres[0]);
        var more = await client.GetMoodCategoryAsync(chill.Shelves[0].More!);

        Assert.Equal("Soundtracks & musicals", soundtracks.Title);
        Assert.Equal("Chill", chill.Title);
        Assert.Equal("Coffee shop blends", more.Title);
        await Assert.ThrowsAsync<HttpRequestException>(
            () => Client(Options(true, "moods")).GetMoodsAndGenresAsync());
    }

    [Fact]
    public async Task HomeMore_FailsOnlyTheLaterPages()
    {
        var client = Client(Options(true, "home-more"));

        var first = await client.GetHomeAsync();

        Assert.NotEmpty(first.Shelves);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetHomeContinuationAsync(first.Continuation!));
    }

    [Theory]
    [InlineData(SearchFilter.Songs, typeof(SongEntry))]
    [InlineData(SearchFilter.Albums, typeof(AlbumEntry))]
    [InlineData(SearchFilter.Artists, typeof(ArtistEntry))]
    [InlineData(SearchFilter.Playlists, typeof(PlaylistEntry))]
    public async Task Search_AnswersWithTheRecordingForTheFilter(SearchFilter filter, Type expected)
    {
        var results = await Client(Options()).SearchAsync("anything", filter);
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.IsType(expected, r));
    }

    [Fact]
    public async Task Search_ForEmpty_ReturnsNoResults()
    {
        Assert.Empty(await Client(Options()).SearchAsync("empty please", SearchFilter.Songs));
    }

    [Fact]
    public async Task SearchSuggestions_AnswerAnyQuery_ExceptEmpty()
    {
        var client = Client(Options(signedIn: false));

        var suggestions = await client.GetSearchSuggestionsAsync("da");
        Assert.NotEmpty(suggestions.Queries);
        Assert.NotEmpty(suggestions.Entries);

        var none = await client.GetSearchSuggestionsAsync("empty please");
        Assert.Empty(none.Queries);
        Assert.Empty(none.Entries);
    }

    [Fact]
    public async Task UnfilteredSearch_HasATopResultAndResults()
    {
        var overview = await Client(Options(signedIn: false)).SearchAllAsync("anything");
        Assert.IsType<ArtistEntry>(overview.TopResult?.Entry);
        Assert.NotEmpty(overview.Results);
    }

    [Fact]
    public async Task FilteredSearch_PagesOnce_ThenEnds()
    {
        var client = Client(Options(signedIn: false));

        var first = await client.SearchPagedAsync("anything", SearchFilter.Songs);
        Assert.NotNull(first.Continuation);

        var second = await client.GetSearchContinuationAsync("anything", SearchFilter.Songs, first.Continuation!);
        Assert.NotEmpty(second.Items);
        Assert.Null(second.Continuation);
    }

    [Fact]
    public async Task EveryPageKind_Loads_AndPlaylistsPageOnward()
    {
        var client = Client(Options());

        var album = await client.GetAlbumAsync("MPREb_anything");
        Assert.NotEmpty(album.Tracks);

        var artist = await client.GetArtistAsync("UCanything");
        Assert.NotEmpty(artist.TopSongs);

        var playlist = await client.GetPlaylistAsync("PLanything");
        Assert.NotEmpty(playlist.Tracks);
        Assert.NotNull(playlist.Continuation);
        var more = await client.GetPlaylistContinuationAsync("PLanything", playlist.Continuation!);
        Assert.NotEmpty(more.Tracks);

        Assert.NotEmpty(await client.GetRadioAsync("anything"));
    }

    [Fact]
    public async Task LibraryAlbumsAndArtists_PageOnce_AndSaveWithoutError()
    {
        var client = Client(Options());

        var albums = await client.GetLibraryAlbumsAsync();
        Assert.All(albums.Items, a => Assert.NotNull(Assert.IsType<AlbumEntry>(a).AudioPlaylistId));
        var moreAlbums = await client.GetLibraryAlbumsContinuationAsync(albums.Continuation!);
        Assert.NotEmpty(moreAlbums.Items);
        Assert.Null(moreAlbums.Continuation);

        var artists = await client.GetLibraryArtistsAsync();
        Assert.All(artists.Items, a => Assert.IsType<ArtistEntry>(a));
        var moreArtists = await client.GetLibraryArtistsContinuationAsync(artists.Continuation!);
        Assert.NotEmpty(moreArtists.Items);

        await client.SetAlbumInLibraryAsync(((AlbumEntry)albums.Items[0]).AudioPlaylistId!, false);
        await client.SetArtistSubscribedAsync(((ArtistEntry)artists.Items[0]).BrowseId, false);
    }

    [Fact]
    public async Task LibrarySongs_PageOnce_AndRemoveWithoutError()
    {
        var client = Client(Options());

        var songs = await client.GetLibrarySongsAsync();
        Assert.All(songs.Items, s => Assert.NotNull(Assert.IsType<SongEntry>(s).Track.Library));
        var more = await client.GetLibrarySongsContinuationAsync(songs.Continuation!);
        Assert.NotEmpty(more.Items);
        Assert.Null(more.Continuation);

        await client.SetSongInLibraryAsync(((SongEntry)songs.Items[0]).Track.Library!, false);
    }

    [Fact]
    public async Task History_LoadsAndRemovesWithoutError()
    {
        var client = Client(Options());

        var history = await client.GetHistoryAsync();
        var entries = history.Items.Select(i => Assert.IsType<HistoryEntry>(i)).ToList();
        Assert.NotEmpty(entries);
        Assert.Null(history.Continuation);

        await client.RemoveFromHistoryAsync(entries[0].RemoveToken!);
    }

    [Fact]
    public async Task TheDemoAccountsOwnPlaylists_AreEditable_AndOthersAreNot()
    {
        var client = Client(Options());
        var library = await client.GetLibraryPlaylistsAsync();
        var own = library.Items.OfType<PlaylistEntry>().First(p => p.Title == "Late Night Coding");

        Assert.True((await client.GetPlaylistAsync(own.PlaylistId)).IsEditable);
        Assert.True((await client.GetPlaylistAsync("PLdemo1")).IsEditable);
        Assert.False((await client.GetPlaylistAsync("LM")).IsEditable);
        Assert.False((await client.GetPlaylistAsync("PLanything")).IsEditable);
    }

    [Fact]
    public async Task PlaylistMove_Succeeds_WhenSignedIn()
    {
        var client = Client(Options());
        var playlist = await client.GetPlaylistAsync("PLanything");
        var rows = playlist.Tracks.Select(t => t.SetVideoId!).ToArray();

        await client.MovePlaylistItemAsync(
            "PLanything", rows[^1], PlaylistMoves.Successor(rows, rows.Length - 1, 0));
    }

    [Fact]
    public async Task Library_AndItsContinuation_Load_WhenSignedIn()
    {
        var client = Client(Options());
        var page = await client.GetLibraryPlaylistsAsync();
        Assert.NotEmpty(page.Items);
        Assert.NotNull(page.Continuation);
        var more = await client.GetLibraryPlaylistsContinuationAsync(page.Continuation!);
        Assert.NotEmpty(more.Items);
    }

    [Fact]
    public async Task Session_IsTheDemoAccount_OrAnonymous()
    {
        var signedIn = await Client(Options()).GetSessionInfoAsync();
        Assert.Equal(SessionState.Valid, signedIn.State);
        Assert.Equal(DemoOptions.AccountName, signedIn.AccountName);

        var anonymous = await Client(Options(signedIn: false)).GetSessionInfoAsync();
        Assert.Equal(SessionState.Anonymous, anonymous.State);
    }

    [Fact]
    public async Task Mutations_Succeed()
    {
        var client = Client(Options());
        await client.RateAsync("x", Rating.Like);
        Assert.StartsWith("PLdemo", await client.CreatePlaylistAsync("Mine"));
        await client.AddToPlaylistAsync("PLdemo1", ["x"]);
        await client.RenamePlaylistAsync("PLdemo1", "Renamed");
        await client.DeletePlaylistAsync("PLdemo1");
    }

    [Fact]
    public async Task AFailingArea_FailsLikeANetworkError_AndOnlyThatArea()
    {
        var client = Client(Options(true, "home"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetHomeAsync());
        Assert.NotEmpty(await client.SearchAsync("x", SearchFilter.Songs));

        var all = Client(Options(true, "all"));
        await Assert.ThrowsAsync<HttpRequestException>(() => all.GetAlbumAsync("MPREb_x"));
    }

    [Fact]
    public async Task AnUnrecordedPage_IsAnError_NotAnEmptyPage()
    {
        await Assert.ThrowsAsync<HttpRequestException>(
            () => Client(Options()).GetAlbumAsync("FEmusic_charts"));
    }

    [Fact]
    public async Task Streams_AreWavFiles_AsLongAsTheTrack()
    {
        var queue = new QueueModel();
        queue.SetQueue([new TrackInfo
        {
            VideoId = "abc", Title = "t", Artists = [new("a")], Duration = TimeSpan.FromSeconds(10),
        }]);
        var dir = Path.Combine(Path.GetTempPath(), "rambutan-demo-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var media = await new DemoStreamSource(queue, dir).ResolveAsync("abc");

            var bytes = await File.ReadAllBytesAsync(media.Url);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal(44 + 10 * 16_000, bytes.Length); // 10 s of 16 kHz mono 8-bit
            Assert.Contains(bytes.Skip(44), b => b != 128); // not silence
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NetworkDrop_FaultsEachTrackOnce_AndRefusesStreamsForTheOutage()
    {
        var queue = new QueueModel();
        queue.SetQueue([new TrackInfo
        {
            VideoId = "abc", Title = "t", Artists = [new("a")], Duration = TimeSpan.FromSeconds(10),
        }]);
        var dir = Path.Combine(Path.GetTempPath(), "rambutan-demo-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var streams = new DemoStreamSource(queue, dir);
            var engine = new StubPlayer();
            using var drop = new DemoNetworkDrop(engine, streams);
            var seen = new List<Rambutan.Core.Playback.AudioPlayerEvent>();
            drop.EventRaised += seen.Add;

            engine.Progress("abc#1", TimeSpan.FromSeconds(5));
            engine.Progress("abc#1", DemoNetworkDrop.DropAt);
            engine.Progress("abc#1", DemoNetworkDrop.DropAt + TimeSpan.FromSeconds(1));

            Assert.True(engine.Paused);
            Assert.Collection(seen,
                e => Assert.False(((Rambutan.Core.Playback.ProgressEvent)e).IsStarved),
                e => Assert.Equal("abc#1", ((Rambutan.Core.Playback.PlayerFaultedEvent)e).Key),
                e => Assert.True(((Rambutan.Core.Playback.ProgressEvent)e).IsStarved));
            await Assert.ThrowsAsync<Rambutan.Core.Playback.OfflineException>(() => streams.ResolveAsync("abc"));

            // The resumed track plays on past the same point.
            seen.Clear();
            engine.Progress("abc#2", DemoNetworkDrop.DropAt + TimeSpan.FromSeconds(2));
            Assert.IsType<Rambutan.Core.Playback.ProgressEvent>(Assert.Single(seen));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class StubPlayer : Rambutan.Core.Playback.IAudioPlayer
    {
        public bool Paused { get; private set; }
        public double Volume { get; set; }
        public event Action<Rambutan.Core.Playback.AudioPlayerEvent>? EventRaised;

        public void Progress(string key, TimeSpan position) =>
            EventRaised?.Invoke(new Rambutan.Core.Playback.ProgressEvent(
                key, position, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), false));

        public Task PlayAsync(Rambutan.Core.Playback.AudioTrackRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task PreloadNextAsync(Rambutan.Core.Playback.AudioTrackRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public void ClearNext() { }
        public void BeginCrossfade(TimeSpan duration) { }
        public Task<Rambutan.Core.Playback.SeekResult> SeekAsync(TimeSpan position, CancellationToken ct = default) => Task.FromResult(new Rambutan.Core.Playback.SeekResult(true, null));
        public void Pause() => Paused = true;
        public void Resume() => Paused = false;
        public void Stop() { }
    }

    [Fact]
    public async Task FailingOnlyTheNextPage_LeavesTheFirstPageLoading()
    {
        var client = Client(Options(true, "playlist-more"));
        var playlist = await client.GetPlaylistAsync("PLanything");
        Assert.NotEmpty(playlist.Tracks);
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetPlaylistContinuationAsync("PLanything", playlist.Continuation!));
    }

    [Fact]
    public async Task SignedIn_TheDemoAccountHasRatedTracks_AnonymousHasNot()
    {
        var signedIn = Client(Options());
        var playlist = await signedIn.GetPlaylistAsync("PLanything");
        var album = await signedIn.GetAlbumAsync("MPREb_anything");

        Assert.Equal([Rating.Like, Rating.Dislike, Rating.None], playlist.Tracks.Take(3).Select(t => t.Rating));
        Assert.Equal(Rating.Like, album.Tracks[0].Rating);

        var anonymous = await Client(Options(signedIn: false)).GetPlaylistAsync("PLanything");
        Assert.All(anonymous.Tracks, t => Assert.Equal(Rating.None, t.Rating));
    }

    [Fact]
    public async Task RatingLookup_SignedOut_IsUnknownWithoutARequest()
    {
        // "radio" failing would surface if the lookup made a request.
        var client = Client(Options(signedIn: false, "radio"));

        Assert.Null(await client.GetRatingAsync("khnokW3Mw24"));
    }

    private static string FindFixtures()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Rambutan.InnerTube.Tests", "Fixtures");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("InnerTube fixtures not found above the test binaries.");
    }
}
