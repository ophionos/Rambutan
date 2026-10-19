using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.App.ViewModels;

public partial class ArtistPageViewModel : ViewModelBase
{
    private readonly ICatalog _catalog;
    private readonly PlaybackCoordinator _coordinator;
    private readonly NavigationService _navigation;
    private readonly string _browseId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenStation))]
    [NotifyCanExecuteChangedFor(nameof(OpenStationCommand))]
    private string _name = "";
    [ObservableProperty] private string? _thumbnailUrl;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private LoadError? _loadError;

    /// <summary>Whether the user subscribes; null (signed out, or the page didn't
    /// say) hides the toggle, as unknown must never read as "not subscribed".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubscribe))]
    private bool? _subscribed;

    public bool CanSubscribe => Subscribed is not null;

    /// <summary>The header's Mix, the artist's radio; null (none, or not loaded
    /// yet) hides its button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartMix))]
    [NotifyCanExecuteChangedFor(nameof(StartMixCommand))]
    private MixSeed? _mix;

    public bool CanStartMix => Mix is not null;

    /// <summary>The header's Shuffle, a mix of the artist's own songs; null (none,
    /// or not loaded yet) hides its button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShuffleSongs))]
    [NotifyCanExecuteChangedFor(nameof(ShuffleSongsCommand))]
    private MixSeed? _shuffle;

    public bool CanShuffleSongs => Shuffle is not null;

    /// <summary>Set by navigation; null in tests, where outcomes go unreported.</summary>
    public TrackActionsViewModel? Actions { get; init; }

    /// <summary>Last.fm stations; null hides the artist's station.</summary>
    public HomeStations? Stations { get; init; }

    // Kept, so the station's page shows the same rows when it opens again.
    private StationShelfViewModel? _station;

    /// <summary>The artist's Last.fm station can open: the stations
    /// setting is on, Last.fm is connected, and the artist's name is known.</summary>
    public bool CanOpenStation => Stations?.User is not null && Name.Length > 0;

    /// <summary>Call when the stations setting or the Last.fm connection may have
    /// changed. The view listens only while it is shown, since pages wait in the
    /// back stack and <see cref="HomeStations"/> lives as long as the app.</summary>
    public void StationsChanged()
    {
        OnPropertyChanged(nameof(CanOpenStation));
        OpenStationCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<TrackInfo> TopSongs { get; } = [];
    public ObservableCollection<Shelf> Shelves { get; } = [];

    public ArtistPageViewModel(
        ICatalog catalog, PlaybackCoordinator coordinator,
        NavigationService navigation, string browseId)
    {
        _catalog = catalog;
        _coordinator = coordinator;
        _navigation = navigation;
        _browseId = browseId;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            var artist = await _catalog.GetArtistAsync(_browseId);
            Name = artist.Name;
            ThumbnailUrl = artist.ThumbnailUrl;
            Subscribed = artist.Subscribed;
            Mix = artist.Mix;
            Shuffle = artist.Shuffle;
            foreach (var track in artist.TopSongs)
                TopSongs.Add(track);
            foreach (var shelf in artist.Shelves)
                Shelves.Add(shelf);
        }
        catch (Exception ex)
        {
            LoadError = LoadError.From(ex, LoadSubject.Artist, ReloadCommand);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    [RelayCommand]
    private Task PlayTopSongsAsync() =>
        TopSongs.Count > 0 ? _coordinator.PlayQueueAsync(TopSongs.ToList()) : Task.CompletedTask;

    /// <summary>Plays the artist's radio. The button waits while the mix loads.</summary>
    [RelayCommand(CanExecute = nameof(CanStartMix))]
    private Task StartMixAsync() => PlayMixAsync(Mix, ArtistMix.Radio);

    /// <summary>Plays YouTube Music's shuffle of the artist's songs. The button
    /// waits while the mix loads.</summary>
    [RelayCommand(CanExecute = nameof(CanShuffleSongs))]
    private Task ShuffleSongsAsync() => PlayMixAsync(Shuffle, ArtistMix.Shuffle);

    private async Task PlayMixAsync(MixSeed? seed, ArtistMix kind)
    {
        if (seed is null)
            return;
        try
        {
            Actions?.Report(Strings.Format(kind.Starting, ("title", Name)));
            var mix = await _catalog.GetMixAsync(seed);
            if (mix.Count == 0)
            {
                Actions?.ReportError(Strings.Format(kind.None, ("name", Name)));
                return;
            }
            await _coordinator.PlayQueueAsync(mix);
            Actions?.Report(Strings.Format(kind.Started, ("title", Name), ("count", mix.Count)));
        }
        catch (Exception ex)
        {
            Actions?.ReportError(Strings.Format(kind.Failed, ("error", ex.Message)));
        }
    }

    /// <summary>Subscribes or unsubscribes, which adds the artist to the library or
    /// takes it out. The toggle flips at once and flips back, with a toast, if
    /// YouTube Music refuses.</summary>
    [RelayCommand]
    private async Task ToggleSubscribedAsync()
    {
        if (Subscribed is not { } was)
            return;

        Subscribed = !was;
        try
        {
            await _catalog.SetArtistSubscribedAsync(_browseId, !was);
            Actions?.NotifyLibraryChanged(LibrarySectionKind.Artists);
        }
        catch (NotSignedInException)
        {
            Subscribed = was;
            Actions?.ReportError(Strings.Get("toast.library.signIn"));
        }
        catch (Exception ex)
        {
            Subscribed = was;
            Actions?.ReportError(Strings.Format("toast.artist.subscribeFailed", ("name", Name), ("error", ex.Message)));
        }
    }

    [RelayCommand]
    private Task PlayFromAsync(TrackInfo track) =>
        _coordinator.PlayQueueAsync(TopSongs.ToList(), Math.Max(0, TopSongs.IndexOf(track)));


    /// <summary>Opens the artist's Last.fm station. It loads only now, never with
    /// the page, as each track costs a YouTube Music search; a station already
    /// loaded opens as it was, and its page's refresh asks for a new one.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenStation))]
    private void OpenStation()
    {
        if (Stations is null)
            return;
        var station = _station ??= new StationShelfViewModel(
            Name, Stations, (tracks, start) => _coordinator.PlayQueueAsync(tracks, start));
        _navigation.OpenStation(station);
        if (!station.IsLoading && station.Items.Count == 0)
            _ = station.LoadAsync();
    }

    [RelayCommand]
    private Task OpenEntryAsync(CatalogEntry entry) => _navigation.OpenEntryAsync(entry);

    /// <summary>A card's hover play button: plays without opening the page.</summary>
    [RelayCommand]
    private Task PlayEntryAsync(CatalogEntry entry) => _navigation.PlayEntryAsync(entry);
}
