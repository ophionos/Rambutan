using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Core.Catalog;
using Rambutan.Core.Downloads;
using Rambutan.Core.Links;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Playlists;

namespace Rambutan.App.ViewModels;

/// <summary>
/// What an album, playlist or artist card does without opening its page: the
/// card's menu and its play button. Most entries load the page first (a
/// playlist's first page for playing, as its page shows before scrolling).
/// While they load, <see cref="IsLoading"/> is true for that album, playlist or
/// artist, on every card that shows it; a failed or empty load says so in a
/// toast. UI thread only.
/// </summary>
public sealed partial class CollectionActionsViewModel
{
    // As on the playlist's page: a bound, in case the server ever repeats a token.
    private const int MaxPlaylistPages = 200;

    private readonly ICatalog _catalog;
    private readonly PlaybackCoordinator _coordinator;
    private readonly TrackActionsViewModel _actions;
    private readonly HomeStations? _stations;
    private readonly Action<StationShelfViewModel>? _openStation;
    private readonly HashSet<string> _loading = [];

    public CollectionActionsViewModel(
        ICatalog catalog, PlaybackCoordinator coordinator, TrackActionsViewModel actions,
        HomeStations? stations = null, Action<StationShelfViewModel>? openStation = null)
    {
        _catalog = catalog;
        _coordinator = coordinator;
        _actions = actions;
        _stations = stations;
        _openStation = openStation;
    }

    /// <summary>An album, playlist or artist started or finished loading.</summary>
    public event Action? LoadingChanged;

    /// <summary>Set at composition: whether YouTube Music has a session. Signed
    /// out, the menu leaves out saving and subscribing; null reads as signed out.</summary>
    public Func<bool>? IsSignedIn { get; set; }

    public bool CanChangeLibrary => IsSignedIn?.Invoke() == true;

    public bool CanDownload => _actions.Downloads is not null;

    /// <summary>A playlist can be copied to one on this computer.</summary>
    public bool CanCopyToLocal => _actions.LocalPlaylists is not null;

    /// <summary>Opens a playlist on this computer by id; set by navigation, so
    /// the copy's toast can open it.</summary>
    public Action<string>? OpenLocalPlaylist { get; set; }

    /// <summary>Whether an artist's Last.fm station can open: only while Last.fm
    /// stations are on and connected, as on the artist's page.</summary>
    public bool CanOpenStation => _stations?.User is not null && _openStation is not null;

    public bool IsLoading(CatalogEntry? entry) => entry is not null && Key(entry) is { } key && _loading.Contains(key);

    /// <summary>The link Share copies; null where there is none to copy.</summary>
    public static string? ShareLinkFor(CatalogEntry entry) => ShareLinks.ForEntry(entry);

    // Concurrent executions allowed: one command serves every card, and another
    // card's load mustn't disable this one's entries.
    /// <summary>Plays an album or playlist, or an artist's top songs.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public Task PlayAsync(CatalogEntry? entry) =>
        LoadThenAsync(entry, "toast.playFailed", tracks => _coordinator.PlayQueueAsync(tracks));

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ShuffleAsync(CatalogEntry? entry) =>
        LoadThenAsync(entry, "toast.playFailed",
            tracks => _coordinator.PlayQueueAsync(EntryPlayback.Shuffled(tracks, Random.Shared)));

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task PlayNextAsync(CatalogEntry? entry) =>
        LoadThenAsync(entry, "toast.collection.queueFailed", tracks =>
        {
            _actions.EnqueueNextAll(tracks);
            _actions.Report(Strings.Format("toast.collection.playingNext",
                ("title", entry!.Title), ("count", tracks.Count)));
            return Task.CompletedTask;
        });

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task AddToQueueAsync(CatalogEntry? entry) =>
        LoadThenAsync(entry, "toast.collection.queueFailed", tracks =>
        {
            _actions.EnqueueAll(tracks);
            _actions.Report(Strings.Format("toast.collection.addedToQueue",
                ("title", entry!.Title), ("count", tracks.Count)));
            return Task.CompletedTask;
        });

    /// <summary>Saves an album or playlist to the library, or subscribes to an
    /// artist. A card doesn't know whether it already is; adding twice is harmless.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SaveToLibraryAsync(CatalogEntry? entry) => SetInLibraryAsync(entry, true);

    /// <summary>Takes an album out of the library, or unsubscribes from an artist.
    /// Offered only where the card is known to be in the library.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RemoveFromLibraryAsync(CatalogEntry? entry) => SetInLibraryAsync(entry, false);

    /// <summary>Downloads a whole album or playlist, every page of it, and keeps a
    /// copy of a live page with the downloads, so it opens offline.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DownloadAllAsync(CatalogEntry? entry)
    {
        var (done, download) = await WhileLoadingAsync(entry, DownloadOfAsync,
            (_, ex) => Strings.Format("toast.download.loadFailed", ("error", ex.Message)));
        if (!done)
            return;
        if (download.Tracks.Count == 0)
        {
            _actions.Report(Strings.Format("toast.nothingToPlay", ("title", entry!.Title)));
            return;
        }
        _actions.DownloadAll(download.Tracks, download.Keep);
    }

    /// <summary>
    /// Copies a YouTube Music playlist to a new one on this computer, every
    /// page of it, duplicates included (maintainer's decision: a copy keeps what
    /// it was copied from). The name is asked for, the playlist's own offered.
    /// The copy never changes the playlist it came from.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task CopyToLocalAsync(CatalogEntry? entry)
    {
        if (entry is not PlaylistEntry playlist || _actions.LocalPlaylists is not { } library)
            return;
        if (library.IsReadOnly)
        {
            _actions.ReportError(TrackActionsViewModel.ReadOnlyNote(library));
            return;
        }
        var (done, loaded) = await WhileLoadingAsync(entry, _ => AllPlaylistTracksAsync(playlist.PlaylistId),
            Failed("toast.localPlaylist.copyFailed"));
        if (!done)
            return;
        if (loaded.Tracks.Count == 0)
        {
            _actions.Report(Strings.Format("toast.localPlaylist.nothingToCopy", ("title", entry.Title)));
            return;
        }
        var title = loaded.Page.Title is { Length: > 0 } t ? t : entry.Title;
        var name = _actions.TextPrompt is null ? title : await _actions.TextPrompt(Strings.Get("prompt.copyPlaylist"), title);
        if (LocalPlaylists.CleanName(name) is not { } clean)
            return;
        try
        {
            var copy = library.Copy(clean, loaded.Tracks, playlist.PlaylistId);
            var message = Strings.Format("toast.localPlaylist.copied", ("count", copy.Items.Count), ("name", copy.Name));
            if (OpenLocalPlaylist is { } open)
                _actions.Toasts.Offer(message, Strings.Get("common.open"), () =>
                {
                    open(copy.Id);
                    return Task.CompletedTask;
                });
            else
                _actions.Report(message);
        }
        catch (LocalPlaylistsReadOnlyException)
        {
            _actions.ReportError(TrackActionsViewModel.ReadOnlyNote(library));
        }
        catch (Exception ex)
        {
            _actions.ReportError(Failed("toast.localPlaylist.copyFailed")(entry, ex));
        }
    }

    /// <summary>Opens an album's artist, which only the album's page names.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task GoToArtistAsync(CatalogEntry? entry)
    {
        if (entry is not AlbumEntry album)
            return;
        var (done, page) = await WhileLoadingAsync(entry, _ => _catalog.GetAlbumAsync(album.BrowseId),
            Failed("toast.album.artistFailed"));
        if (!done)
            return;
        if (page.ArtistBrowseId is { } artist)
            _actions.OpenArtist?.Invoke(artist);
        else
            _actions.Report(Strings.Format("toast.album.noArtist", ("title", album.Title)));
    }

    /// <summary>Plays an artist's radio: the Mix its page's header starts, so the
    /// page loads first.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task StartMixAsync(CatalogEntry? entry) => PlayArtistMixAsync(entry, ArtistMix.Radio);

    /// <summary>Plays the Shuffle an artist's page's header starts, a mix of the
    /// artist's own songs, so the page loads first.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ShuffleSongsAsync(CatalogEntry? entry) => PlayArtistMixAsync(entry, ArtistMix.Shuffle);

    private async Task PlayArtistMixAsync(CatalogEntry? entry, ArtistMix kind)
    {
        if (entry is not ArtistEntry artist)
            return;
        var (done, mix) = await WhileLoadingAsync(entry, async _ =>
            kind.SeedOf(await _catalog.GetArtistAsync(artist.BrowseId)) is { } seed
                ? await _catalog.GetMixAsync(seed)
                : [],
            (_, ex) => Strings.Format(kind.Failed, ("error", ex.Message)));
        if (!done)
            return;
        if (mix.Count == 0)
        {
            _actions.ReportError(Strings.Format(kind.None, ("name", artist.Title)));
            return;
        }

        try
        {
            await _coordinator.PlayQueueAsync(mix);
            _actions.Report(Strings.Format(kind.Started, ("title", artist.Title), ("count", mix.Count)));
        }
        catch (Exception ex)
        {
            _actions.ReportError(Strings.Format(kind.Failed, ("error", ex.Message)));
        }
    }

    /// <summary>Opens the Last.fm station of an artist and similar ones; its page
    /// shows the loading.</summary>
    [RelayCommand]
    private void OpenStation(CatalogEntry? entry)
    {
        if (entry is not ArtistEntry artist || _stations is null || _openStation is null)
            return;
        _openStation(new StationShelfViewModel(
            artist.Title, _stations, (tracks, start) => _coordinator.PlayQueueAsync(tracks, start)));
    }

    [RelayCommand]
    private async Task ShareAsync(CatalogEntry? entry)
    {
        if (entry is null || ShareLinkFor(entry) is not { } link || _actions.CopyToClipboard is not { } copy)
            return;

        await copy(link);
        _actions.Report(Strings.Format("toast.track.linkCopied", ("title", entry.Title)));
    }

    private async Task SetInLibraryAsync(CatalogEntry? entry, bool add)
    {
        var (done, section) = await WhileLoadingAsync(entry, async e =>
        {
            switch (e)
            {
                case AlbumEntry album:
                    // Not every shelf carries the album's own playlist, which is
                    // what saving targets; its page always does.
                    var id = album.AudioPlaylistId
                        ?? (await _catalog.GetAlbumAsync(album.BrowseId)).AudioPlaylistId
                        ?? throw new InvalidOperationException(Strings.Get("card.noAlbumPlaylist"));
                    await _catalog.SetAlbumInLibraryAsync(id, add);
                    return LibrarySectionKind.Albums;
                case PlaylistEntry playlist:
                    await _catalog.SetPlaylistInLibraryAsync(playlist.PlaylistId, add);
                    return LibrarySectionKind.Playlists;
                case ArtistEntry artist:
                    await _catalog.SetArtistSubscribedAsync(artist.BrowseId, add);
                    return LibrarySectionKind.Artists;
                default:
                    throw new NotSupportedException();
            }
        }, (e, ex) => ex is NotSignedInException
            ? Strings.Get("toast.library.signIn")
            : e is ArtistEntry
                ? Strings.Format("toast.artist.subscribeFailed", ("name", e.Title), ("error", ex.Message))
                : Strings.Format("toast.library.saveFailed", ("title", e.Title), ("error", ex.Message)));
        if (!done)
            return;

        _actions.NotifyLibraryChanged(section);
        if (!add)
            _actions.NotifyRemovedFromLibrary(section, entry!);
        _actions.Report(entry is ArtistEntry
            ? Strings.Format(add ? "toast.artist.subscribedTo" : "toast.artist.unsubscribedFrom", ("name", entry.Title))
            : Strings.Format(add ? "toast.library.saved" : "toast.library.removed", ("title", entry!.Title)));
    }

    /// <summary>The tracks to download, and the copy to keep: none for a page
    /// that is itself the kept copy.</summary>
    private async Task<(IReadOnlyList<TrackInfo> Tracks, KeptCollection? Keep)> DownloadOfAsync(CatalogEntry entry)
    {
        switch (entry)
        {
            case AlbumEntry album:
            {
                var page = await _catalog.GetAlbumAsync(album.BrowseId);
                var kept = KeptCollection.ForAlbum(album.BrowseId, page.Title, page.Artist, page.ArtistBrowseId,
                    page.SubtitleInfo, page.ThumbnailUrl,
                    page.Tracks.Select(t => AlbumAudio.Prefer(t, _actions.DownloadedIds.Contains)),
                    DateTimeOffset.UtcNow);
                return (kept.Tracks, page.KeptAt is null ? kept : null);
            }
            case PlaylistEntry playlist:
            {
                var (page, tracks) = await AllPlaylistTracksAsync(playlist.PlaylistId);
                var owner = PlaylistPageViewModel.OwnerOf(page);
                return (tracks, page.KeptAt is null
                    ? new KeptCollection(KeptCollectionKind.Playlist, playlist.PlaylistId, page.Title,
                        owner?.Name, owner?.BrowseId, page.SubtitleInfo, page.ThumbnailUrl, tracks, DateTimeOffset.UtcNow)
                    : null);
            }
            default:
                return ([], null);
        }
    }

    /// <summary>The playlist's header and every page of its tracks.</summary>
    private async Task<(PlaylistPage Page, List<TrackInfo> Tracks)> AllPlaylistTracksAsync(string playlistId)
    {
        var page = await _catalog.GetPlaylistAsync(playlistId);
        var tracks = page.Tracks.ToList();
        var next = page.Continuation;
        for (var n = 0; n < MaxPlaylistPages && next is not null; n++)
        {
            var more = await _catalog.GetPlaylistContinuationAsync(playlistId, next);
            tracks.AddRange(more.Tracks);
            next = more.Continuation;
        }
        return (page, tracks);
    }

    /// <summary>Loads the entry's tracks, then hands them on.</summary>
    private async Task LoadThenAsync(
        CatalogEntry? entry, string failedKey, Func<IReadOnlyList<TrackInfo>, Task> then)
    {
        var (done, tracks) = await WhileLoadingAsync(entry,
            e => EntryPlayback.ResolveAsync(_catalog, e, _actions.DownloadedIds.Contains), Failed(failedKey));
        if (!done)
            return;

        if (tracks.Count == 0)
        {
            _actions.Report(Strings.Format("toast.nothingToPlay", ("title", entry!.Title)));
            return;
        }

        try
        {
            await then(tracks);
        }
        catch (Exception ex)
        {
            _actions.ReportError(Failed(failedKey)(entry!, ex));
        }
    }

    /// <summary>Runs <paramref name="work"/> while the cards showing the entry say
    /// it is loading. Not done when the entry is already loading (a second
    /// request is dropped: the card shows it is busy) or the work failed, which
    /// <paramref name="failed"/> words for a toast.</summary>
    private async Task<(bool Done, T Value)> WhileLoadingAsync<T>(
        CatalogEntry? entry, Func<CatalogEntry, Task<T>> work, Func<CatalogEntry, Exception, string> failed)
    {
        if (entry is null || Key(entry) is not { } key || !_loading.Add(key))
            return default;

        LoadingChanged?.Invoke();
        try
        {
            return (true, await work(entry));
        }
        catch (Exception ex)
        {
            _actions.ReportError(failed(entry, ex));
            return default;
        }
        finally
        {
            _loading.Remove(key);
            LoadingChanged?.Invoke();
        }
    }

    private static Func<CatalogEntry, Exception, string> Failed(string key) =>
        (entry, ex) => Strings.Format(key, ("title", entry.Title), ("error", ex.Message));

    private static string? Key(CatalogEntry entry) => entry switch
    {
        AlbumEntry album => "album:" + album.BrowseId,
        PlaylistEntry playlist => "playlist:" + playlist.PlaylistId,
        ArtistEntry artist => "artist:" + artist.BrowseId,
        _ => null,
    };
}
