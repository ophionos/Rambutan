using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>
/// Search calls stay pending until the test completes them, so tests control the
/// order in which overlapping searches finish. Cancelling a call's token cancels
/// its task, as a real HTTP request would. Albums and playlists return what a test
/// sets. Home pages stay pending like searches (<see cref="HomeCalls"/>). Playlist
/// row edits, saves and subscriptions are recorded; Library pages stay pending
/// (<see cref="LibraryCalls"/>); ratings succeed unless <see cref="Rate"/> says
/// otherwise. Everything else is unused.
/// </summary>
internal sealed class FakeCatalog : ICatalog
{
    /// <summary>A search; <see cref="Filter"/> null is the unfiltered "All"
    /// search, which answers with <see cref="Top"/> as its top result. A filtered
    /// search answers with <see cref="Next"/> as its next page's token;
    /// <see cref="From"/> is the token a later page was asked for with.</summary>
    public sealed record SearchCall(
        string Query, SearchFilter? Filter, CancellationToken Token,
        TaskCompletionSource<IReadOnlyList<CatalogEntry>> Result, ContinuationToken? From = null)
    {
        public TopResult? Top { get; set; }
        public ContinuationToken? Next { get; set; }

        /// <summary>Answers with these rows and a next page.</summary>
        public void CompleteWithMore(string next, params string[] titles)
        {
            Next = new ContinuationToken(next);
            Complete(titles);
        }

        public void Complete(params string[] titles) =>
            Result.TrySetResult(titles.Select(Entry).ToArray());

        public void Fail(string message) => Result.TrySetException(new HttpRequestException(message));
    }

    public List<SearchCall> Searches { get; } = [];

    public Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        string query, SearchFilter filter, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<CatalogEntry>>();
        ct.Register(() => tcs.TrySetCanceled(ct));
        Searches.Add(new SearchCall(query, filter, ct, tcs));
        return tcs.Task;
    }

    public static CatalogEntry Entry(string title) =>
        new AlbumEntry { Title = title, BrowseId = title };

    public async Task<SearchOverview> SearchAllAsync(string query, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<CatalogEntry>>();
        ct.Register(() => tcs.TrySetCanceled(ct));
        var call = new SearchCall(query, null, ct, tcs);
        Searches.Add(call);
        var results = await tcs.Task;
        return new SearchOverview { TopResult = call.Top, Results = results };
    }
    public Task<SearchPage> SearchPagedAsync(string query, SearchFilter filter, CancellationToken ct = default) =>
        PageAsync(query, filter, from: null, ct);

    public Task<SearchPage> GetSearchContinuationAsync(string query, SearchFilter filter, ContinuationToken continuation, CancellationToken ct = default) =>
        PageAsync(query, filter, continuation, ct);

    private async Task<SearchPage> PageAsync(
        string query, SearchFilter filter, ContinuationToken? from, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<CatalogEntry>>();
        ct.Register(() => tcs.TrySetCanceled(ct));
        var call = new SearchCall(query, filter, ct, tcs, from);
        Searches.Add(call);
        var items = await tcs.Task;
        return new SearchPage { Items = items, Continuation = call.Next };
    }
    /// <summary>A suggestions request, pending until the test answers it.</summary>
    public sealed record SuggestCall(string Query, CancellationToken Token, TaskCompletionSource<SearchSuggestions> Result)
    {
        public void Complete(string[] queries, params CatalogEntry[] entries) =>
            Result.TrySetResult(new SearchSuggestions
            {
                Queries = queries.Select(q => new SearchSuggestion(q)).ToArray(),
                Entries = entries,
            });
    }

    public List<SuggestCall> Suggestions { get; } = [];

    public Task<SearchSuggestions> GetSearchSuggestionsAsync(string query, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<SearchSuggestions>();
        ct.Register(() => tcs.TrySetCanceled(ct));
        Suggestions.Add(new SuggestCall(query, ct, tcs));
        return tcs.Task;
    }
    public Task<IReadOnlyList<TrackInfo>> SearchSongsAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
    /// <summary>A Home request: the first page when <see cref="Token"/> is null, else more.</summary>
    public sealed record HomeCall(ContinuationToken? Token, TaskCompletionSource<HomePage> Result)
    {
        /// <summary>The mood chip the first page was filtered to; null for plain Home.</summary>
        public HomeChip? Chip { get; init; }

        /// <summary>Answers with one shelf per title, and <paramref name="next"/> as the token for more.</summary>
        public void Complete(string? next, params string[] shelves) =>
            CompleteWithChips([], next, shelves);

        /// <summary>As <see cref="Complete(string?, string[])"/>, with mood chips above the shelves.</summary>
        public void CompleteWithChips(IReadOnlyList<HomeChip> chips, string? next, params string[] shelves) =>
            Result.TrySetResult(new HomePage
            {
                Shelves = shelves.Select(t => new Shelf(t, [Entry(t)])).ToArray(),
                Continuation = next is null ? null : new ContinuationToken(next),
                Chips = chips,
            });

        public void Fail(string message) => Result.TrySetException(new HttpRequestException(message));
    }

    public List<HomeCall> HomeCalls { get; } = [];

    public Task<HomePage> GetHomeAsync(CancellationToken ct = default) => Home(null);
    public Task<HomePage> GetHomeAsync(HomeChip chip, CancellationToken ct = default) => Home(null, chip);
    public Task<HomePage> GetHomeContinuationAsync(ContinuationToken continuation, CancellationToken ct = default) =>
        Home(continuation);

    private Task<HomePage> Home(ContinuationToken? token, HomeChip? chip = null)
    {
        var call = new HomeCall(token, new TaskCompletionSource<HomePage>()) { Chip = chip };
        HomeCalls.Add(call);
        return call.Result.Task;
    }

    /// <summary>What the Moods &amp; genres grid returns; unset, it fails as unsupported.</summary>
    public Func<Task<MoodsPage>>? Moods { get; set; }
    /// <summary>What a mood or genre page returns; unset, it fails as unsupported.</summary>
    public Func<MoodCategory, Task<MoodCategoryPage>>? MoodPage { get; set; }

    public Task<MoodsPage> GetMoodsAndGenresAsync(CancellationToken ct = default) =>
        Moods is { } moods ? moods() : throw new NotSupportedException();
    public Task<MoodCategoryPage> GetMoodCategoryAsync(MoodCategory category, CancellationToken ct = default) =>
        MoodPage is { } page ? page(category) : throw new NotSupportedException();
    /// <summary>What album and playlist fetches return; unset, they fail as unsupported.</summary>
    public AlbumPage? Album { get; set; }
    public PlaylistPage? Playlist { get; set; }

    /// <summary>Holds album fetches until it completes; unset, they answer at once.</summary>
    public Task? AlbumGate { get; set; }

    public async Task<AlbumPage> GetAlbumAsync(string browseId, CancellationToken ct = default)
    {
        if (AlbumGate is { } gate)
            await gate;
        return Album ?? throw new NotSupportedException();
    }
    public Task<PlaylistPage> GetPlaylistAsync(string playlistId, CancellationToken ct = default) =>
        Playlist is { } playlist ? Task.FromResult(playlist) : throw new NotSupportedException();
    /// <summary>What a playlist's next page returns; unset, it fails as unsupported.</summary>
    public Func<ContinuationToken, Task<PlaylistPage>>? PlaylistMore { get; set; }

    public Task<PlaylistPage> GetPlaylistContinuationAsync(string playlistId, ContinuationToken continuation, CancellationToken ct = default) =>
        PlaylistMore is { } more ? more(continuation) : throw new NotSupportedException();
    /// <summary>What artist fetches return; unset, they fail as unsupported.</summary>
    public ArtistPage? Artist { get; set; }

    /// <summary>Holds artist fetches until it completes; unset, they answer at once.</summary>
    public Task? ArtistGate { get; set; }

    public async Task<ArtistPage> GetArtistAsync(string browseId, CancellationToken ct = default)
    {
        if (ArtistGate is { } gate)
            await gate;
        return Artist ?? throw new NotSupportedException();
    }
    public Func<string, Task<IReadOnlyList<TrackInfo>>>? Radio { get; set; }

    public Task<IReadOnlyList<TrackInfo>> GetRadioAsync(string videoId, CancellationToken ct = default) =>
        Radio is { } radio ? radio(videoId) : throw new NotSupportedException();

    /// <summary>A Library tab's request ("playlists", "albums", "artists" or "songs"): the
    /// first page when <see cref="Token"/> is null, else the page after it. Pending
    /// until the test answers it.</summary>
    public sealed record LibraryCall(string What, ContinuationToken? Token, TaskCompletionSource<LibraryPage> Result)
    {
        /// <summary>Answers with one entry per title, and <paramref name="next"/> as the token for more.</summary>
        public void Complete(string? next, params string[] titles) =>
            Result.TrySetResult(new LibraryPage
            {
                Items = titles.Select(Entry).ToArray(),
                Continuation = next is null ? null : new ContinuationToken(next),
            });

        /// <summary>Answers with these entries as they are (song rows, mixed kinds).</summary>
        public void Complete(string? next, IReadOnlyList<CatalogEntry> entries) =>
            Result.TrySetResult(new LibraryPage
            {
                Items = entries,
                Continuation = next is null ? null : new ContinuationToken(next),
            });

        public void Fail(string message) => Result.TrySetException(new HttpRequestException(message));

        public void FailSignedOut() => Result.TrySetException(new NotSignedInException());
    }

    public List<LibraryCall> LibraryCalls { get; } = [];

    private Task<LibraryPage> Library(string what, ContinuationToken? token)
    {
        var call = new LibraryCall(what, token, new TaskCompletionSource<LibraryPage>());
        LibraryCalls.Add(call);
        return call.Result.Task;
    }

    public Task<LibraryPage> GetLibraryPlaylistsAsync(CancellationToken ct = default) => Library("playlists", null);
    public Task<LibraryPage> GetLibraryPlaylistsContinuationAsync(ContinuationToken continuation, CancellationToken ct = default) => Library("playlists", continuation);
    public Task<LibraryPage> GetLibraryAlbumsAsync(CancellationToken ct = default) => Library("albums", null);
    public Task<LibraryPage> GetLibraryAlbumsContinuationAsync(ContinuationToken continuation, CancellationToken ct = default) => Library("albums", continuation);
    public Task<LibraryPage> GetLibraryArtistsAsync(CancellationToken ct = default) => Library("artists", null);
    public Task<LibraryPage> GetLibraryArtistsContinuationAsync(ContinuationToken continuation, CancellationToken ct = default) => Library("artists", continuation);
    public Task<LibraryPage> GetLibrarySongsAsync(CancellationToken ct = default) => Library("songs", null);
    public Task<LibraryPage> GetLibrarySongsContinuationAsync(ContinuationToken continuation, CancellationToken ct = default) => Library("songs", continuation);

    // Saves and subscriptions complete at once and are recorded in LibraryEdits;
    // EditFailure makes them throw, as it does playlist edits.
    public List<string> LibraryEdits { get; } = [];

    public Task SetAlbumInLibraryAsync(string audioPlaylistId, bool inLibrary, CancellationToken ct = default)
    {
        if (EditFailure is { } failure)
            return Task.FromException(failure);
        LibraryEdits.Add($"{(inLibrary ? "save" : "remove")} {audioPlaylistId}");
        return Task.CompletedTask;
    }

    public Task SetPlaylistInLibraryAsync(string playlistId, bool inLibrary, CancellationToken ct = default)
    {
        if (EditFailure is { } failure)
            return Task.FromException(failure);
        LibraryEdits.Add($"{(inLibrary ? "save" : "remove")} playlist {playlistId}");
        return Task.CompletedTask;
    }

    /// <summary>When set, song saves wait on it (so a test can look mid-request).</summary>
    public Task? SongEditGate { get; set; }

    public async Task SetSongInLibraryAsync(LibraryToggle toggle, bool inLibrary, CancellationToken ct = default)
    {
        if (SongEditGate is { } gate)
            await gate;
        if (EditFailure is { } failure)
            throw failure;
        LibraryEdits.Add(inLibrary ? $"add {toggle.AddToken}" : $"remove {toggle.RemoveToken}");
    }

    public Task SetArtistSubscribedAsync(string artistBrowseId, bool subscribed, CancellationToken ct = default)
    {
        if (EditFailure is { } failure)
            return Task.FromException(failure);
        LibraryEdits.Add($"{(subscribed ? "subscribe" : "unsubscribe")} {artistBrowseId}");
        return Task.CompletedTask;
    }

    /// <summary>What RateAsync does; records the call and succeeds by default.</summary>
    public Func<string, Rating, Task>? Rate { get; set; }
    public List<(string VideoId, Rating Rating)> Rated { get; } = [];

    public Task RateAsync(string videoId, Rating rating, CancellationToken ct = default)
    {
        Rated.Add((videoId, rating));
        return Rate?.Invoke(videoId, rating) ?? Task.CompletedTask;
    }

    public Task<Rating?> GetRatingAsync(string videoId, CancellationToken ct = default) => Task.FromResult<Rating?>(null);

    public Task<string> CreatePlaylistAsync(string title, string? description = null, PlaylistPrivacy privacy = PlaylistPrivacy.Private, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RenamePlaylistAsync(string playlistId, string title, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AddToPlaylistAsync(string playlistId, IReadOnlyList<string> videoIds, CancellationToken ct = default) => throw new NotSupportedException();

    // Playlist row edits complete at once and are recorded. Set EditFailure to make
    // the next edits throw, as an offline request would.
    public List<string> PlaylistEdits { get; } = [];
    public Exception? EditFailure { get; set; }
    public Func<string, string?> NewSetVideoId { get; set; } = videoId => $"new-{videoId}";

    public Task RemoveFromPlaylistAsync(string playlistId, string videoId, string setVideoId, CancellationToken ct = default)
    {
        if (EditFailure is { } failure)
            return Task.FromException(failure);
        PlaylistEdits.Add($"remove {setVideoId}");
        return Task.CompletedTask;
    }

    public Task MovePlaylistItemAsync(string playlistId, string setVideoId, string? beforeSetVideoId, CancellationToken ct = default)
    {
        if (EditFailure is { } failure)
            return Task.FromException(failure);
        PlaylistEdits.Add($"move {setVideoId} before {beforeSetVideoId ?? "end"}");
        return Task.CompletedTask;
    }

    public Task<string?> InsertIntoPlaylistAsync(string playlistId, string videoId, string? beforeSetVideoId, CancellationToken ct = default)
    {
        if (EditFailure is { } failure)
            return Task.FromException<string?>(failure);
        PlaylistEdits.Add($"insert {videoId} before {beforeSetVideoId ?? "end"}");
        return Task.FromResult(NewSetVideoId(videoId));
    }
}
