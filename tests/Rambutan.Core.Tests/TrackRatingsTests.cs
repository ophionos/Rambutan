using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.Core.Tests;

public class TrackRatingsTests
{
    private static TrackInfo Track(string id = "v1", Rating? rating = null) =>
        new() { VideoId = id, Title = id, Artists = [], Rating = rating };

    [Theory]
    [InlineData(Rating.None, Rating.Like, Rating.Like)]
    [InlineData(Rating.Like, Rating.Like, Rating.None)]
    [InlineData(Rating.Dislike, Rating.Like, Rating.Like)]
    [InlineData(Rating.None, Rating.Dislike, Rating.Dislike)]
    [InlineData(Rating.Dislike, Rating.Dislike, Rating.None)]
    [InlineData(Rating.Like, Rating.Dislike, Rating.Dislike)]
    public void Toggle_PressingTheCurrentRatingClearsIt(Rating current, Rating pressed, Rating expected) =>
        Assert.Equal(expected, TrackRatings.Toggle(current, pressed));

    [Fact]
    public void Of_FallsBackToTheSourceSnapshot()
    {
        var ratings = new TrackRatings(new RatingCatalog());

        Assert.Equal(Rating.Like, ratings.Of(Track(rating: Rating.Like)));
        Assert.Null(ratings.Of(Track(rating: null)));
    }

    [Fact]
    public async Task Toggle_OnALikedTrack_SendsRemoveAndWinsOverTheSnapshot()
    {
        var catalog = new RatingCatalog();
        var ratings = new TrackRatings(catalog);
        var track = Track(rating: Rating.Like);

        var result = await ratings.ToggleAsync(track, Rating.Like);

        Assert.Equal(Rating.None, result);
        Assert.Equal([("v1", Rating.None)], catalog.Sent);
        // Every other record of the same song (queue, other pages) agrees.
        Assert.Equal(Rating.None, ratings.Of(Track(rating: Rating.Like)));
    }

    [Fact]
    public async Task Toggle_AppliesBeforeTheRequestCompletes()
    {
        var catalog = new RatingCatalog { Gate = new TaskCompletionSource() };
        var ratings = new TrackRatings(catalog);
        var changed = new List<string>();
        ratings.Changed += changed.Add;

        var pending = ratings.ToggleAsync(Track(), Rating.Like);

        Assert.False(pending.IsCompleted);
        Assert.Equal(Rating.Like, ratings.Get("v1"));
        Assert.Equal(["v1"], changed);

        catalog.Gate.SetResult();
        await pending;
        Assert.Equal(Rating.Like, ratings.Get("v1"));
    }

    [Fact]
    public async Task Toggle_Failure_RevertsToTheSnapshotAndRethrows()
    {
        var catalog = new RatingCatalog { Failure = new HttpRequestException("offline") };
        var ratings = new TrackRatings(catalog);
        var changed = new List<string>();
        ratings.Changed += changed.Add;
        var track = Track(rating: Rating.Like);

        await Assert.ThrowsAsync<HttpRequestException>(() => ratings.ToggleAsync(track, Rating.Like));

        Assert.Null(ratings.Get("v1"));
        Assert.Equal(Rating.Like, ratings.Of(track));
        Assert.Equal(["v1", "v1"], changed); // applied, then undone
    }

    [Fact]
    public async Task Toggle_Failure_RevertsToThePreviousSessionValue()
    {
        var catalog = new RatingCatalog();
        var ratings = new TrackRatings(catalog);
        await ratings.ToggleAsync(Track(), Rating.Dislike);

        catalog.Failure = new NotSignedInException();
        await Assert.ThrowsAsync<NotSignedInException>(() => ratings.ToggleAsync(Track(), Rating.Like));

        Assert.Equal(Rating.Dislike, ratings.Get("v1"));
    }

    [Fact]
    public async Task Toggle_FailureAfterANewerPress_LeavesTheNewerPress()
    {
        var catalog = new RatingCatalog { Gate = new TaskCompletionSource() };
        var ratings = new TrackRatings(catalog);

        var first = ratings.ToggleAsync(Track(), Rating.Like);       // → Like, held
        catalog.Gate = null;
        await ratings.ToggleAsync(Track(), Rating.Dislike);          // → Dislike, sent

        // The first request now fails; undoing it would clobber the newer press.
        var held = catalog.Held!;
        catalog.Held = null;
        held.SetException(new HttpRequestException("late failure"));
        await Assert.ThrowsAsync<HttpRequestException>(() => first);

        Assert.Equal(Rating.Dislike, ratings.Get("v1"));
    }

    [Fact]
    public async Task LookUp_UnknownTrack_AsksTheCatalogAndRemembers()
    {
        var catalog = new RatingCatalog { Lookup = Rating.Like };
        var ratings = new TrackRatings(catalog);
        var changed = new List<string>();
        ratings.Changed += changed.Add;

        Assert.Equal(Rating.Like, await ratings.LookUpAsync(Track()));
        Assert.Equal(Rating.Like, ratings.Of(Track()));
        Assert.Equal(["v1"], changed);
        Assert.Equal(1, catalog.Lookups);
    }

    [Fact]
    public async Task LookUp_KnownTrack_DoesNotAsk()
    {
        var catalog = new RatingCatalog { Lookup = Rating.Like };
        var ratings = new TrackRatings(catalog);

        Assert.Equal(Rating.None, await ratings.LookUpAsync(Track(rating: Rating.None)));
        Assert.Equal(0, catalog.Lookups);
    }

    [Fact]
    public async Task LookUp_CatalogCantSay_StaysUnknown()
    {
        var ratings = new TrackRatings(new RatingCatalog { Lookup = null });

        Assert.Null(await ratings.LookUpAsync(Track()));
        Assert.Null(ratings.Get("v1"));
    }

    [Fact]
    public async Task LookUp_PressDuringLookup_ThePressWins()
    {
        var catalog = new RatingCatalog { Lookup = Rating.None, LookupGate = new TaskCompletionSource() };
        var ratings = new TrackRatings(catalog);

        var lookup = ratings.LookUpAsync(Track());
        await ratings.ToggleAsync(Track(), Rating.Like);
        catalog.LookupGate.SetResult();

        Assert.Equal(Rating.Like, await lookup);
        Assert.Equal(Rating.Like, ratings.Get("v1"));
    }

    /// <summary>Rating calls only; everything else is unused.</summary>
    // SongLibraryTests share it: library saves go through the same gate and failure.
    internal sealed class RatingCatalog : ICatalog
    {
        public List<(LibraryToggle, bool)> SongEdits { get; } = [];

        public async Task SetSongInLibraryAsync(LibraryToggle toggle, bool inLibrary, CancellationToken ct = default)
        {
            SongEdits.Add((toggle, inLibrary));
            if (Gate is { } gate)
            {
                Held = gate;
                await gate.Task;
            }
            if (Failure is { } failure)
                throw failure;
        }

        public List<(string, Rating)> Sent { get; } = [];

        /// <summary>What album, playlist and library page loads throw (OfflineCatalogTests).</summary>
        public Exception? PageFailure { get; set; }
        public Exception? Failure { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public TaskCompletionSource? Held { get; set; }
        public Rating? Lookup { get; set; }
        public TaskCompletionSource? LookupGate { get; set; }
        public int Lookups { get; private set; }

        public async Task RateAsync(string videoId, Rating rating, CancellationToken ct = default)
        {
            Sent.Add((videoId, rating));
            if (Gate is { } gate)
            {
                Held = gate;
                await gate.Task;
            }
            if (Failure is { } failure)
                throw failure;
        }

        public async Task<Rating?> GetRatingAsync(string videoId, CancellationToken ct = default)
        {
            Lookups++;
            if (LookupGate is { } gate)
                await gate.Task;
            return Lookup;
        }

        public Task<IReadOnlyList<TrackInfo>> SearchSongsAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SearchOverview> SearchAllAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SearchPage> SearchPagedAsync(string query, SearchFilter filter, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SearchPage> GetSearchContinuationAsync(string query, SearchFilter filter, ContinuationToken continuation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SearchSuggestions> GetSearchSuggestionsAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogEntry>> SearchAsync(string query, SearchFilter filter, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HomePage> GetHomeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HomePage> GetHomeAsync(HomeChip chip, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MoodsPage> GetMoodsAndGenresAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MoodCategoryPage> GetMoodCategoryAsync(MoodCategory category, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HomePage> GetHomeContinuationAsync(ContinuationToken continuation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AlbumPage> GetAlbumAsync(string browseId, CancellationToken ct = default) => throw PageFailure ?? new NotSupportedException();
        public Task<PlaylistPage> GetPlaylistAsync(string playlistId, CancellationToken ct = default) => throw PageFailure ?? new NotSupportedException();
        public Task<PlaylistPage> GetPlaylistContinuationAsync(string playlistId, ContinuationToken continuation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArtistPage> GetArtistAsync(string browseId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrackInfo>> GetRadioAsync(string videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrackInfo>> GetMixAsync(MixSeed seed, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryPage> GetLibraryPlaylistsAsync(CancellationToken ct = default) => throw PageFailure ?? new NotSupportedException();
        public Task<LibraryPage> GetLibraryAlbumsAsync(CancellationToken ct = default) => throw PageFailure ?? new NotSupportedException();
        public Task<LibraryPage> GetLibraryAlbumsContinuationAsync(ContinuationToken continuation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryPage> GetLibraryArtistsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryPage> GetLibraryArtistsContinuationAsync(ContinuationToken continuation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAlbumInLibraryAsync(string audioPlaylistId, bool inLibrary, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetPlaylistInLibraryAsync(string playlistId, bool inLibrary, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetArtistSubscribedAsync(string artistBrowseId, bool subscribed, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryPage> GetLibrarySongsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryPage> GetLibrarySongsContinuationAsync(ContinuationToken continuation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryPage> GetLibraryPlaylistsContinuationAsync(ContinuationToken continuation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryPage> GetHistoryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveFromHistoryAsync(string removeToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> CreatePlaylistAsync(string title, string? description = null, PlaylistPrivacy privacy = PlaylistPrivacy.Private, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RenamePlaylistAsync(string playlistId, string title, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddToPlaylistAsync(string playlistId, IReadOnlyList<string> videoIds, bool keepDuplicates = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveFromPlaylistAsync(string playlistId, string videoId, string setVideoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MovePlaylistItemAsync(string playlistId, string setVideoId, string? beforeSetVideoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> InsertIntoPlaylistAsync(string playlistId, string videoId, string? beforeSetVideoId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
