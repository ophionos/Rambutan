using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Converters;
using Rambutan.App.Localization;
using Rambutan.Core.Catalog;
using Rambutan.Core.Downloads;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.App.ViewModels;

public partial class AlbumPageViewModel : ViewModelBase
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

    public AlbumPageViewModel(ICatalog catalog, PlaybackCoordinator coordinator, string browseId)
    {
        _catalog = catalog;
        _coordinator = coordinator;
        _browseId = browseId;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
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
        }
        catch (Exception ex)
        {
            LoadError = LoadError.From(ex, LoadSubject.Album, ReloadCommand);
        }
        finally
        {
            IsLoading = false;
        }
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
