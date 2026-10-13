using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.Core.Downloads;

/// <summary>
/// The catalog, with the kept copies of downloaded albums and playlists behind it.
/// When an album or playlist page can't be fetched and a copy exists, the
/// copy is returned, marked with <see cref="AlbumPage.KeptAt"/>; when the
/// library's albums or playlists can't be fetched, the kept ones are listed,
/// marked with <see cref="LibraryPage.IsOfflineCopy"/>. A copy is read-only: it
/// claims no library state and no ownership, and has no further pages.
/// Everything else goes straight to the catalog.
/// </summary>
/// <param name="localCover">A track's kept cover as a URL the UI can load, or
/// null; the header of a copy uses its first track that has one, since the
/// remote thumbnail can't load offline.</param>
public sealed class OfflineCatalog(
    ICatalog inner, OfflineCollections kept, Func<string, string?>? localCover = null) : ICatalog
{
    public async Task<AlbumPage> GetAlbumAsync(string browseId, CancellationToken ct = default)
    {
        try
        {
            return await inner.GetAlbumAsync(browseId, ct);
        }
        catch (Exception ex) when (FallsBack(ex, ct) && kept.Find(KeptCollectionKind.Album, browseId) is { } copy)
        {
            return new AlbumPage
            {
                BrowseId = copy.Id,
                Title = copy.Title,
                Artist = copy.Artist,
                ArtistBrowseId = copy.ArtistBrowseId,
                SubtitleInfo = copy.SubtitleInfo,
                ThumbnailUrl = Cover(copy),
                Tracks = copy.Tracks,
                KeptAt = copy.KeptAt,
            };
        }
    }

    public async Task<PlaylistPage> GetPlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        try
        {
            return await inner.GetPlaylistAsync(playlistId, ct);
        }
        catch (Exception ex) when (FallsBack(ex, ct) && kept.Find(KeptCollectionKind.Playlist, playlistId) is { } copy)
        {
            return new PlaylistPage
            {
                PlaylistId = copy.Id,
                Title = copy.Title,
                SubtitleInfo = copy.SubtitleInfo,
                Author = copy.Artist is { } owner ? new ArtistRef(owner, copy.ArtistBrowseId) : null,
                ThumbnailUrl = Cover(copy),
                Tracks = copy.Tracks,
                KeptAt = copy.KeptAt,
            };
        }
    }

    public async Task<LibraryPage> GetLibraryPlaylistsAsync(CancellationToken ct = default)
    {
        try
        {
            return await inner.GetLibraryPlaylistsAsync(ct);
        }
        catch (Exception ex) when (FallsBackForLibrary(ex, ct) && kept.List(KeptCollectionKind.Playlist) is { Count: > 0 } copies)
        {
            return new LibraryPage
            {
                Items = copies.Select(c => (CatalogEntry)new PlaylistEntry
                {
                    PlaylistId = c.Id, Title = c.Title, Subtitle = c.SubtitleInfo, ThumbnailUrl = Cover(c),
                }).ToArray(),
                IsOfflineCopy = true,
            };
        }
    }

    public async Task<LibraryPage> GetLibraryAlbumsAsync(CancellationToken ct = default)
    {
        try
        {
            return await inner.GetLibraryAlbumsAsync(ct);
        }
        catch (Exception ex) when (FallsBackForLibrary(ex, ct) && kept.List(KeptCollectionKind.Album) is { Count: > 0 } copies)
        {
            return new LibraryPage
            {
                Items = copies.Select(c => (CatalogEntry)new AlbumEntry
                {
                    BrowseId = c.Id, Title = c.Title, Subtitle = c.Artist, ThumbnailUrl = Cover(c),
                }).ToArray(),
                IsOfflineCopy = true,
            };
        }
    }

    /// <summary>Any failure but the caller's own cancellation: a copy with a
    /// notice and a retry beats an error, whatever kept the page from loading.</summary>
    private static bool FallsBack(Exception ex, CancellationToken ct) =>
        !(ex is OperationCanceledException && ct.IsCancellationRequested);

    /// <summary>Signed out, the library shows its sign-in state, not the copies.</summary>
    private static bool FallsBackForLibrary(Exception ex, CancellationToken ct) =>
        FallsBack(ex, ct) && ex is not NotSignedInException;

    private string? Cover(KeptCollection copy) =>
        copy.Tracks.Select(t => localCover?.Invoke(t.VideoId)).FirstOrDefault(c => c is not null)
        ?? copy.ThumbnailUrl;

    public Task<IReadOnlyList<TrackInfo>> GetRadioAsync(string videoId, CancellationToken ct = default) =>
        inner.GetRadioAsync(videoId, ct);

    public Task<IReadOnlyList<TrackInfo>> SearchSongsAsync(string query, CancellationToken ct = default) =>
        inner.SearchSongsAsync(query, ct);

    public Task<IReadOnlyList<CatalogEntry>> SearchAsync(string query, SearchFilter filter, CancellationToken ct = default) =>
        inner.SearchAsync(query, filter, ct);

    public Task<SearchOverview> SearchAllAsync(string query, CancellationToken ct = default) =>
        inner.SearchAllAsync(query, ct);

    public Task<SearchPage> SearchPagedAsync(string query, SearchFilter filter, CancellationToken ct = default) =>
        inner.SearchPagedAsync(query, filter, ct);

    public Task<SearchPage> GetSearchContinuationAsync(
        string query, SearchFilter filter, ContinuationToken continuation, CancellationToken ct = default) =>
        inner.GetSearchContinuationAsync(query, filter, continuation, ct);

    public Task<SearchSuggestions> GetSearchSuggestionsAsync(string query, CancellationToken ct = default) =>
        inner.GetSearchSuggestionsAsync(query, ct);

    public Task<HomePage> GetHomeAsync(CancellationToken ct = default) => inner.GetHomeAsync(ct);

    public Task<HomePage> GetHomeAsync(HomeChip chip, CancellationToken ct = default) => inner.GetHomeAsync(chip, ct);

    public Task<MoodsPage> GetMoodsAndGenresAsync(CancellationToken ct = default) => inner.GetMoodsAndGenresAsync(ct);

    public Task<MoodCategoryPage> GetMoodCategoryAsync(MoodCategory category, CancellationToken ct = default) =>
        inner.GetMoodCategoryAsync(category, ct);

    public Task<HomePage> GetHomeContinuationAsync(ContinuationToken continuation, CancellationToken ct = default) =>
        inner.GetHomeContinuationAsync(continuation, ct);

    public Task<PlaylistPage> GetPlaylistContinuationAsync(
        string playlistId, ContinuationToken continuation, CancellationToken ct = default) =>
        inner.GetPlaylistContinuationAsync(playlistId, continuation, ct);

    public Task<ArtistPage> GetArtistAsync(string browseId, CancellationToken ct = default) =>
        inner.GetArtistAsync(browseId, ct);

    public Task<LibraryPage> GetLibraryPlaylistsContinuationAsync(
        ContinuationToken continuation, CancellationToken ct = default) =>
        inner.GetLibraryPlaylistsContinuationAsync(continuation, ct);

    public Task<LibraryPage> GetLibraryAlbumsContinuationAsync(
        ContinuationToken continuation, CancellationToken ct = default) =>
        inner.GetLibraryAlbumsContinuationAsync(continuation, ct);

    public Task<LibraryPage> GetLibraryArtistsAsync(CancellationToken ct = default) => inner.GetLibraryArtistsAsync(ct);

    public Task<LibraryPage> GetLibraryArtistsContinuationAsync(
        ContinuationToken continuation, CancellationToken ct = default) =>
        inner.GetLibraryArtistsContinuationAsync(continuation, ct);

    public Task<LibraryPage> GetLibrarySongsAsync(CancellationToken ct = default) => inner.GetLibrarySongsAsync(ct);

    public Task<LibraryPage> GetLibrarySongsContinuationAsync(
        ContinuationToken continuation, CancellationToken ct = default) =>
        inner.GetLibrarySongsContinuationAsync(continuation, ct);

    public Task SetSongInLibraryAsync(LibraryToggle toggle, bool inLibrary, CancellationToken ct = default) =>
        inner.SetSongInLibraryAsync(toggle, inLibrary, ct);

    public Task SetAlbumInLibraryAsync(string audioPlaylistId, bool inLibrary, CancellationToken ct = default) =>
        inner.SetAlbumInLibraryAsync(audioPlaylistId, inLibrary, ct);

    public Task SetArtistSubscribedAsync(string artistBrowseId, bool subscribed, CancellationToken ct = default) =>
        inner.SetArtistSubscribedAsync(artistBrowseId, subscribed, ct);

    public Task RateAsync(string videoId, Rating rating, CancellationToken ct = default) =>
        inner.RateAsync(videoId, rating, ct);

    public Task<Rating?> GetRatingAsync(string videoId, CancellationToken ct = default) =>
        inner.GetRatingAsync(videoId, ct);

    public Task<string> CreatePlaylistAsync(
        string title, string? description = null,
        PlaylistPrivacy privacy = PlaylistPrivacy.Private, CancellationToken ct = default) =>
        inner.CreatePlaylistAsync(title, description, privacy, ct);

    public Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default) =>
        inner.DeletePlaylistAsync(playlistId, ct);

    public Task RenamePlaylistAsync(string playlistId, string title, CancellationToken ct = default) =>
        inner.RenamePlaylistAsync(playlistId, title, ct);

    public Task AddToPlaylistAsync(string playlistId, IReadOnlyList<string> videoIds, CancellationToken ct = default) =>
        inner.AddToPlaylistAsync(playlistId, videoIds, ct);

    public Task RemoveFromPlaylistAsync(
        string playlistId, string videoId, string setVideoId, CancellationToken ct = default) =>
        inner.RemoveFromPlaylistAsync(playlistId, videoId, setVideoId, ct);

    public Task MovePlaylistItemAsync(
        string playlistId, string setVideoId, string? beforeSetVideoId, CancellationToken ct = default) =>
        inner.MovePlaylistItemAsync(playlistId, setVideoId, beforeSetVideoId, ct);

    public Task<string?> InsertIntoPlaylistAsync(
        string playlistId, string videoId, string? beforeSetVideoId, CancellationToken ct = default) =>
        inner.InsertIntoPlaylistAsync(playlistId, videoId, beforeSetVideoId, ct);
}
