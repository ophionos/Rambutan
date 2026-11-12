using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Converters;
using Rambutan.App.Localization;
using Rambutan.Core.Catalog;
using Rambutan.Core.Downloads;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Sidebar;

namespace Rambutan.App.ViewModels;

public partial class AlbumPageViewModel : ViewModelBase, IPinnablePage
{
    private readonly ICatalog _catalog;
    private readonly PlaybackCoordinator _coordinator;
    private readonly string _browseId;

    [ObservableProperty] private string _title = "";
    /// <summary>The header artist; a link when it carries a browse id.</summary>
    [ObservableProperty] private ArtistRef? _artist;
    /// <summary>"Album • 2013".</summary>
    [ObservableProperty] private string _details = "";
    /// <summary>"13 songs • 1 hr 14 min", from the rows themselves.</summary>
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _thumbnailUrl;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private LoadError? _loadError;

    /// <summary>The page is the copy kept with the downloads, because the catalog
    /// couldn't be reached; null on a live page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeptNotice))]
    private DateTimeOffset? _keptAt;

    /// <summary>"Offline copy from …": says the page may be out of date and that
    /// songs not downloaded won't play.</summary>
    public string? KeptNotice => KeptAt is { } at ? Strings.Format("page.keptCopy", ("date", at.ToLocalTime().ToString("d"))) : null;

    /// <summary>Whether the album is in the library; null (signed out, or the page
    /// didn't say) hides the toggle, as unknown must never read as "not saved".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(IsSaved))]
    private bool? _inLibrary;

    private string? _audioPlaylistId;
    private string? _subtitleInfo;

    public bool CanSave => InLibrary is not null && _audioPlaylistId is not null;

    public bool IsSaved => InLibrary == true;

    public ObservableCollection<TrackInfo> Tracks { get; } = [];

    /// <summary>Set by navigation; null (tests) hides the download button.</summary>
    public TrackActionsViewModel? Actions { get; init; }

    public bool CanDownload => Actions?.Downloads is not null;

    private CollectionDownloadButton? _downloadButton;

    /// <summary>The header's Download button; null where downloads aren't offered.</summary>
    public CollectionDownloadButton? DownloadButton =>
        _downloadButton ??= Actions is { Downloads: not null } actions
            ? new CollectionDownloadButton(
                actions, Tracks, () => (Tracks.Select(t => t.VideoId), true),
                () =>
                {
                    DownloadAll();
                    return Task.CompletedTask;
                },
                () => Title, "album.downloadTip")
            : null;

    /// <summary>Set by navigation; null (tests) offers no pinning.</summary>
    public CollectionActionsViewModel? Collections { get; init; }

    public PinKind PinKind => PinKind.Album;
    public string PinId => _browseId;

    private SidebarPinToggle? _pin;

    /// <summary>The header's Pin entry; null where pins aren't composed.</summary>
    public SidebarPinToggle? Pin =>
        _pin ??= Collections is { Pins: not null } collections
            ? new SidebarPinToggle(collections, PinKind.Album, _browseId, () => Title, () => ThumbnailUrl)
            : null;

    /// <summary>Set by navigation; null (tests) leaves the side column's cards inert.</summary>
    public NavigationService? Navigation { get; init; }

    // ---- The side column: more by the album's artist, beside the rows. ----

    /// <summary>Whether the pane has room for a column beside the rows; the view
    /// says so, and says false when the page leaves the screen. The artist is
    /// asked for only while this holds, and a request still running when it
    /// stops holding is cancelled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSideColumn))]
    private bool _hasSideRoom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSideColumn))]
    private bool _isLoadingArtist;

    /// <summary>The artist couldn't be loaded: a line in the column, with Try
    /// again, until it is dismissed. Never the page's own error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSideColumn))]
    private LoadError? _artistError;

    /// <summary>The artist's top songs, as the artist page lists them.</summary>
    public ObservableCollection<TrackInfo> ArtistSongs { get; } = [];

    /// <summary>The artist's shelves the column shows (<see cref="MoreByArtist"/>).</summary>
    public ObservableCollection<Shelf> ArtistShelves { get; } = [];

    /// <summary>"Top songs by Daft Punk".</summary>
    public string ArtistSongsTitle => Strings.Format("album.topSongsBy", ("artist", Artist?.Name ?? ""));

    /// <summary>A live page whose header links its artist, with its tracks on
    /// screen. Various Artists has no link, and the copy kept with the downloads
    /// is shown because YouTube Music can't be reached.</summary>
    private bool CanAskArtist => !IsLoading && KeptAt is null && Tracks.Count > 0 && Artist?.BrowseId is not null;

    /// <summary>The column shows while it has something to say: skeletons, the
    /// failure line, or the artist's songs and shelves. An artist page with none
    /// of them leaves no column.</summary>
    public bool ShowsSideColumn =>
        HasSideRoom && CanAskArtist &&
        (IsLoadingArtist || ArtistError is not null || ArtistSongs.Count > 0 || ArtistShelves.Count > 0);

    private CancellationTokenSource? _artistRequest;
    private bool _artistAsked;

    partial void OnHasSideRoomChanged(bool value)
    {
        if (value)
            _ = LoadArtistAsync();
        else
            _artistRequest?.Cancel();
    }

    /// <summary>Asks for the artist once per load of the page, and only when the
    /// column will show. A request the page cancelled is asked again when the
    /// room comes back.</summary>
    private async Task LoadArtistAsync()
    {
        if (!HasSideRoom || !CanAskArtist || _artistAsked || Artist?.BrowseId is not { } artistId)
            return;

        _artistAsked = true;
        var request = _artistRequest = new CancellationTokenSource();
        ArtistError = null;
        IsLoadingArtist = true;
        try
        {
            var artist = await _catalog.GetArtistAsync(artistId, request.Token);
            if (!ReferenceEquals(request, _artistRequest))
                return;
            var more = MoreByArtist.From(artist, _browseId);
            foreach (var song in more.TopSongs)
                ArtistSongs.Add(song);
            foreach (var shelf in more.Shelves)
                ArtistShelves.Add(shelf);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (ReferenceEquals(request, _artistRequest))
                _artistAsked = false;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(request, _artistRequest))
                ArtistError = LoadError.From(ex, LoadSubject.Artist, RetryArtistCommand);
        }
        finally
        {
            if (ReferenceEquals(request, _artistRequest))
            {
                _artistRequest = null;
                IsLoadingArtist = false;
            }
            request.Dispose();
        }
    }

    /// <summary>Forgets the artist, for a load of the page over this one.</summary>
    private void ResetArtist()
    {
        _artistRequest?.Cancel();
        _artistRequest = null;
        _artistAsked = false;
        ArtistSongs.Clear();
        ArtistShelves.Clear();
        ArtistError = null;
        IsLoadingArtist = false;
    }

    [RelayCommand]
    private Task RetryArtistAsync()
    {
        _artistAsked = false;
        ArtistError = null;
        return LoadArtistAsync();
    }

    /// <summary>Takes the failure line away; the column goes with it, until the
    /// page loads again.</summary>
    [RelayCommand]
    private void DismissArtistError() => ArtistError = null;

    [RelayCommand]
    private Task PlayArtistSongFromAsync(TrackInfo track) =>
        _coordinator.PlayQueueAsync(ArtistSongs.ToList(), Math.Max(0, ArtistSongs.IndexOf(track)));

    [RelayCommand]
    private Task OpenEntryAsync(CatalogEntry entry) => Navigation?.OpenEntryAsync(entry) ?? Task.CompletedTask;

    /// <summary>A card's hover play button: plays without opening the page.</summary>
    [RelayCommand]
    private Task PlayEntryAsync(CatalogEntry entry) => Navigation?.PlayEntryAsync(entry) ?? Task.CompletedTask;

    public AlbumPageViewModel(ICatalog catalog, PlaybackCoordinator coordinator, string browseId)
    {
        _catalog = catalog;
        _coordinator = coordinator;
        _browseId = browseId;
        ArtistSongs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowsSideColumn));
        ArtistShelves.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowsSideColumn));
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
        ResetArtist();
        try
        {
            var album = await _catalog.GetAlbumAsync(_browseId);
            // A retry from a kept copy loads over it.
            Tracks.Clear();
            KeptAt = album.KeptAt;
            Title = album.Title;
            Artist = string.IsNullOrEmpty(album.Artist) ? null : new ArtistRef(album.Artist, album.ArtistBrowseId);
            Details = album.SubtitleInfo ?? "";
            _subtitleInfo = album.SubtitleInfo;
            ThumbnailUrl = album.ThumbnailUrl;
            _audioPlaylistId = album.AudioPlaylistId;
            InLibrary = album.InLibrary;
            // Rows play the album audio, not the music videos a signed-out page
            // lists; a music video already downloaded stays as it is.
            Func<string, bool>? hasDownload = Actions is { } actions ? actions.DownloadedIds.Contains : null;
            foreach (var track in album.Tracks)
                Tracks.Add(AlbumAudio.Prefer(track, hasDownload));
            // An album page holds every track, so the count and running time are
            // ours to add up, in the same format the rows use.
            Summary = DurationFormat.Summary(Tracks);
            Pin?.Refresh();
        }
        catch (Exception ex)
        {
            LoadError = LoadError.From(ex, LoadSubject.Album, ReloadCommand);
        }
        finally
        {
            IsLoading = false;
        }

        // The artist comes after the album's own tracks are on screen.
        OnPropertyChanged(nameof(ArtistSongsTitle));
        OnPropertyChanged(nameof(ShowsSideColumn));
        _ = LoadArtistAsync();
    }

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    [RelayCommand]
    private Task PlayAllAsync() =>
        Tracks.Count > 0 ? _coordinator.PlayQueueAsync(Tracks.ToList()) : Task.CompletedTask;

    /// <summary>Plays the album in a random order; the page keeps its own order.</summary>
    [RelayCommand]
    private Task ShuffleAsync() =>
        Tracks.Count > 0
            ? _coordinator.PlayQueueAsync(EntryPlayback.Shuffled(Tracks, Random.Shared))
            : Task.CompletedTask;

    /// <summary>A live page is kept with the downloads, so it opens offline.</summary>
    [RelayCommand]
    private void DownloadAll()
    {
        var kept = KeptCollection.ForAlbum(_browseId, Title, Artist?.Name, Artist?.BrowseId,
            _subtitleInfo, ThumbnailUrl, Tracks, DateTimeOffset.UtcNow);
        Actions?.DownloadAll(kept.Tracks, KeptAt is null ? kept : null);
    }

    /// <summary>Saves the album to the library or removes it. The toggle flips at
    /// once and flips back, with a toast, if YouTube Music refuses.</summary>
    [RelayCommand]
    private async Task ToggleLibraryAsync()
    {
        if (InLibrary is not { } was || _audioPlaylistId is not { } id)
            return;

        InLibrary = !was;
        try
        {
            await _catalog.SetAlbumInLibraryAsync(id, !was);
            Actions?.NotifyLibraryChanged(LibrarySectionKind.Albums);
            Actions?.Report(Strings.Format(was ? "toast.library.removed" : "toast.library.saved", ("title", Title)));
        }
        catch (NotSignedInException)
        {
            InLibrary = was;
            Actions?.ReportError(Strings.Get("toast.library.signIn"));
        }
        catch (Exception ex)
        {
            InLibrary = was;
            Actions?.ReportError(Strings.Format("toast.library.saveFailed", ("title", Title), ("error", ex.Message)));
        }
    }

    [RelayCommand]
    private Task PlayFromAsync(TrackInfo track) =>
        _coordinator.PlayQueueAsync(Tracks.ToList(), Math.Max(0, Tracks.IndexOf(track)));

}
