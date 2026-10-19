using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.Core.Catalog;

public enum SearchFilter { Songs, Videos, Albums, Artists, Playlists }

public enum PlaylistPrivacy { Private, Unlisted, Public }

public interface ICatalog : IRadioSource
{
    Task<IReadOnlyList<TrackInfo>> SearchSongsAsync(string query, CancellationToken ct = default);

    Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        string query, SearchFilter filter, CancellationToken ct = default);

    /// <summary>
    /// An unfiltered search: the top-result card and a preview shelf per type,
    /// each naming the filter behind its "Show all".
    /// </summary>
    Task<SearchOverview> SearchAllAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// The first page of a filtered search, with the token for the next page.
    /// <see cref="SearchAsync"/> is this page's items.
    /// </summary>
    Task<SearchPage> SearchPagedAsync(
        string query, SearchFilter filter, CancellationToken ct = default);

    /// <summary>
    /// The next page of a filtered search; pass the same query and filter as the
    /// first page. Returns an empty page when exhausted.
    /// </summary>
    Task<SearchPage> GetSearchContinuationAsync(
        string query, SearchFilter filter, ContinuationToken continuation, CancellationToken ct = default);

    /// <summary>
    /// Completions for a partly typed query. Empty for a blank query;
    /// works anonymously, and signed in it leads with the user's own history.
    /// </summary>
    Task<SearchSuggestions> GetSearchSuggestionsAsync(string query, CancellationToken ct = default);

    Task<HomePage> GetHomeAsync(CancellationToken ct = default);

    /// <summary>Home filtered to one of its mood chips.</summary>
    Task<HomePage> GetHomeAsync(HomeChip chip, CancellationToken ct = default);

    /// <summary>More of Home's shelves; returns an empty page when exhausted.</summary>
    Task<HomePage> GetHomeContinuationAsync(ContinuationToken continuation, CancellationToken ct = default);

    Task<MoodsPage> GetMoodsAndGenresAsync(CancellationToken ct = default);

    /// <summary>One mood or genre's page, from a tile or a shelf's "More".</summary>
    Task<MoodCategoryPage> GetMoodCategoryAsync(MoodCategory category, CancellationToken ct = default);

    Task<AlbumPage> GetAlbumAsync(string browseId, CancellationToken ct = default);

    Task<PlaylistPage> GetPlaylistAsync(string playlistId, CancellationToken ct = default);

    /// <summary>Fetches more tracks for a playlist; returns an empty page when exhausted.</summary>
    Task<PlaylistPage> GetPlaylistContinuationAsync(
        string playlistId, ContinuationToken continuation, CancellationToken ct = default);

    Task<ArtistPage> GetArtistAsync(string browseId, CancellationToken ct = default);

    // Library and mutations need a signed-in session. Implementations
    // throw NotSignedInException up front rather than surfacing empty pages.

    Task<LibraryPage> GetLibraryPlaylistsAsync(CancellationToken ct = default);

    Task<LibraryPage> GetLibraryPlaylistsContinuationAsync(
        ContinuationToken continuation, CancellationToken ct = default);

    /// <summary>Saved albums, newest first; AlbumEntry.AudioPlaylistId is set.</summary>
    Task<LibraryPage> GetLibraryAlbumsAsync(CancellationToken ct = default);

    Task<LibraryPage> GetLibraryAlbumsContinuationAsync(
        ContinuationToken continuation, CancellationToken ct = default);

    /// <summary>Subscribed artists; BrowseId is the artist's channel (UC…).</summary>
    Task<LibraryPage> GetLibraryArtistsAsync(CancellationToken ct = default);

    Task<LibraryPage> GetLibraryArtistsContinuationAsync(
        ContinuationToken continuation, CancellationToken ct = default);

    /// <summary>Songs saved to the library (not the Liked Music playlist), as track rows.</summary>
    Task<LibraryPage> GetLibrarySongsAsync(CancellationToken ct = default);

    Task<LibraryPage> GetLibrarySongsContinuationAsync(
        ContinuationToken continuation, CancellationToken ct = default);

    /// <summary>
    /// Adds a song to the library or removes it, with the tokens its row carried,
    /// as YTM's per-song "Save to library" toggle does.
    /// </summary>
    Task SetSongInLibraryAsync(LibraryToggle toggle, bool inLibrary, CancellationToken ct = default);

    /// <summary>
    /// Adds an album to the library or removes it, by its AudioPlaylistId (from
    /// the album page or entry), as YTM's "Save to library" toggle does.
    /// </summary>
    Task SetAlbumInLibraryAsync(string audioPlaylistId, bool inLibrary, CancellationToken ct = default);

    /// <summary>
    /// Adds someone else's playlist to the library or removes it, as YTM's "Save
    /// to library" on a playlist does. Takes the playlist's id, with or without
    /// its <c>VL</c> browse prefix.
    /// </summary>
    Task SetPlaylistInLibraryAsync(string playlistId, bool inLibrary, CancellationToken ct = default);

    /// <summary>Subscribes to an artist (adding it to the library) or unsubscribes.</summary>
    Task SetArtistSubscribedAsync(string artistBrowseId, bool subscribed, CancellationToken ct = default);

    Task RateAsync(string videoId, Rating rating, CancellationToken ct = default);

    /// <summary>
    /// The signed-in user's rating of one track, for when the surface that
    /// produced the track didn't report it (search rows, radio rows). Null when
    /// signed out or when the response doesn't say; never throws NotSignedIn.
    /// </summary>
    Task<Rating?> GetRatingAsync(string videoId, CancellationToken ct = default);

    Task<string> CreatePlaylistAsync(
        string title, string? description = null,
        PlaylistPrivacy privacy = PlaylistPrivacy.Private, CancellationToken ct = default);

    Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default);

    Task RenamePlaylistAsync(string playlistId, string title, CancellationToken ct = default);

    Task AddToPlaylistAsync(
        string playlistId, IReadOnlyList<string> videoIds, CancellationToken ct = default);

    /// <summary>Removes one row; setVideoId comes from the playlist page's TrackInfo.</summary>
    Task RemoveFromPlaylistAsync(
        string playlistId, string videoId, string setVideoId, CancellationToken ct = default);

    /// <summary>
    /// Moves the row <paramref name="setVideoId"/> so it sits directly before the row
    /// <paramref name="beforeSetVideoId"/>; null moves it to the end.
    /// Rows are named by setVideoId, never by index, so a stale view can't move the
    /// wrong row. <see cref="PlaylistMoves.Successor"/> turns a drag from one index
    /// to another into the row to pass here.
    /// </summary>
    Task MovePlaylistItemAsync(
        string playlistId, string setVideoId, string? beforeSetVideoId, CancellationToken ct = default);

    /// <summary>
    /// Adds one track and moves the new row before the row <paramref name="beforeSetVideoId"/>
    /// (null leaves it at the end): how Undo puts a removed row back where it was.
    /// Returns the new row's setVideoId, or null when the server didn't report one,
    /// in which case the track was added but left at the end.
    /// </summary>
    Task<string?> InsertIntoPlaylistAsync(
        string playlistId, string videoId, string? beforeSetVideoId, CancellationToken ct = default);
}

/// <summary>An authed-only catalog operation was attempted without a valid session.</summary>
public sealed class NotSignedInException()
    : InvalidOperationException("This action requires signing in to YouTube Music.");
