using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.App.ViewModels;

public partial class HomePageViewModel : ViewModelBase
{
    private readonly ICatalog _catalog;
    private readonly NavigationService _navigation;
    private readonly HomeStations? _stations;
    private readonly Func<IReadOnlyList<TrackInfo>, int, Task>? _play;
    private List<StationShelfViewModel> _stationShelves = [];
    private bool _loaded;
    private ContinuationToken? _continuation;
    // The mood chip Home is filtered to; null for plain Home.
    private HomeChip? _filter;
    // Bumped by each refresh, so a page of shelves requested before it is dropped.
    private int _generation;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private LoadError? _loadError;
    /// <summary>YTM has more shelves to send (it sends a few per page).</summary>
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _isLoadingMore;
    /// <summary>The last "more shelves" request failed; shown where the list ends.</summary>
    [ObservableProperty] private LoadError? _moreError;

    /// <summary>YouTube Music's shelves, as it sent them.</summary>
    public ObservableCollection<Shelf> Shelves { get; } = [];

    /// <summary>The mood chips above the shelves, as the last page sent them;
    /// the one Home is filtered to is <see cref="HomeChip.IsSelected"/>.</summary>
    public ObservableCollection<HomeChip> Chips { get; } = [];

    /// <summary>What the page lists: <see cref="Shelves"/>, with the Last.fm
    /// station shelves (<see cref="StationShelfViewModel"/>) after the first
    /// <see cref="StationsAfter"/> of them while those are on.</summary>
    public ObservableCollection<object> Items { get; } = [];

    /// <summary>Station shelves go after this many YouTube Music shelves: high
    /// enough to be seen without scrolling far, after YTM's own picks.</summary>
    public const int StationsAfter = 2;

    // Design-time
    public HomePageViewModel() : this(null!, null!) { }

    public HomePageViewModel(
        ICatalog catalog, NavigationService navigation,
        HomeStations? stations = null, Func<IReadOnlyList<TrackInfo>, int, Task>? play = null)
    {
        _catalog = catalog;
        _navigation = navigation;
        _stations = stations;
        _play = play;
        if (stations is not null)
            stations.Changed += OnStationsChanged;
    }

    public IReadOnlyList<StationShelfViewModel> StationShelves => _stationShelves;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded || _catalog is null)
            return;
        _loaded = true;
        await LoadPageAsync(freshStations: false);
    }

    /// <summary>
    /// The account changed, so the shelves and chips on show are the old one's.
    /// On screen, they load again now (and stay until the new ones come);
    /// otherwise the next <see cref="EnsureLoadedAsync"/> loads them. A chip
    /// filter goes: the chips are the old account's too.
    /// </summary>
    public void OnSignInChanged(bool onScreen)
    {
        if (_catalog is null)
            return;
        _filter = null;
        if (onScreen)
        {
            _loaded = true;
            _ = LoadPageAsync(freshStations: false);
        }
        else
        {
            _loaded = false;
        }
    }

    /// <summary>Home's own refresh: YouTube Music's shelves, and a fresh
    /// sample of each station.</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadPageAsync(freshStations: true);

    /// <summary>
    /// A mood chip: filters Home to it, or back to plain Home when it is the
    /// one already selected. The shelves on show stay until the new ones come.
    /// </summary>
    [RelayCommand]
    private Task SelectChipAsync(HomeChip chip)
    {
        _filter = chip.IsSelected ? null : chip;
        return LoadPageAsync(freshStations: false);
    }

    [RelayCommand]
    private void OpenMoods() => _navigation.OpenMoods();

    private async Task LoadPageAsync(bool freshStations)
    {
        IsLoading = true;
        LoadError = null;
        var generation = ++_generation;
        try
        {
            var page = _filter is { } chip
                ? await _catalog.GetHomeAsync(chip)
                : await _catalog.GetHomeAsync();
            // A later chip or refresh was asked for meanwhile; its page wins.
            if (generation != _generation)
                return;
            Chips.Clear();
            foreach (var each in page.Chips)
                Chips.Add(each);
            Shelves.Clear();
            foreach (var shelf in page.Shelves)
                Shelves.Add(shelf);
            RebuildItems(freshStations);
            _continuation = page.Continuation;
            HasMore = _continuation is not null;
            MoreError = null;
            IsLoadingMore = false;
        }
        catch (Exception ex)
        {
            if (generation != _generation)
                return;
            var error = LoadError.From(ex, LoadSubject.Home, RefreshCommand);
            // Offline, Home is a dead end; downloads and the Library's kept
            // copies aren't, so point there.
            if (error.IsOffline && _navigation?.GoDownloads is { } goDownloads)
                error = error with
                {
                    Hint = Strings.Get("error.load.offline.homeHint"),
                    GoToDownloads = new RelayCommand(goDownloads),
                };
            LoadError = error;
            _loaded = false;
            // YouTube Music's shelves didn't come, but the Last.fm lists saved
            // last time can still show (and play what's downloaded or cached).
            if (Shelves.Count == 0 && HasSavedStations())
                RebuildItems(freshStations: false);
        }
        finally
        {
            if (generation == _generation)
                IsLoading = false;
        }
    }

    /// <summary>
    /// The next few shelves; the view calls this as the list nears its end, as
    /// YTM's own client does. A no-op while one is in flight or none are left.
    /// </summary>
    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (_continuation is not { } token || IsLoadingMore || IsLoading)
            return;

        var generation = _generation;
        IsLoadingMore = true;
        MoreError = null;
        try
        {
            var page = await _catalog.GetHomeContinuationAsync(token);
            if (generation != _generation)
                return;
            foreach (var shelf in page.Shelves)
            {
                Shelves.Add(shelf);
                Items.Add(shelf);
            }
            // Fewer than StationsAfter shelves came first: the stations were at the end, and move up.
            if (_stationShelves.Count > 0 && Items.IndexOf(_stationShelves[0]) is var at && at < StationsAfter && at < Shelves.Count)
                RebuildItems(freshStations: false);
            _continuation = page.Continuation;
            HasMore = _continuation is not null;
        }
        catch (Exception ex) when (generation == _generation)
        {
            MoreError = LoadError.From(ex, LoadSubject.MoreShelves, LoadMoreCommand);
        }
        finally
        {
            if (generation == _generation)
                IsLoadingMore = false;
        }
    }

    /// <summary>
    /// Lays out <see cref="Items"/> again from <see cref="Shelves"/> and the
    /// stations that are on. Station shelves are reused while their station stays
    /// on, so their cards don't flicker. A new one shows its saved list, or
    /// loads one; <paramref name="freshStations"/> asks every one for a fresh
    /// sample (Home's own refresh).
    /// </summary>
    private void RebuildItems(bool freshStations)
    {
        var wanted = StationsWanted ? HomeStations.Shown : [];
        var kept = new List<StationShelfViewModel>();
        foreach (var kind in wanted)
        {
            var existing = _stationShelves.FirstOrDefault(s => s.Kind == kind);
            var shelf = existing ?? new StationShelfViewModel(kind, _stations!, _play!);
            kept.Add(shelf);
            if (freshStations)
                _ = shelf.LoadAsync();
            else if (existing is null)
                _ = shelf.ShowAsync();
        }
        foreach (var gone in _stationShelves.Except(kept))
            gone.Cancel();
        _stationShelves = kept;

        Items.Clear();
        var at = Math.Min(StationsAfter, Shelves.Count);
        for (var i = 0; i < Shelves.Count; i++)
        {
            if (i == at)
                AddStations();
            Items.Add(Shelves[i]);
        }
        if (at == Shelves.Count)
            AddStations();

        void AddStations()
        {
            foreach (var shelf in kept)
                Items.Add(shelf);
        }
    }

    private bool HasSavedStations() =>
        StationsWanted && HomeStations.Shown.Any(kind => _stations!.Saved(kind) is not null);

    /// <summary>Stations show on plain Home only: a mood chip is YouTube Music's pick for that mood.</summary>
    private bool StationsWanted => _stations?.User is not null && _play is not null && _filter is null;

    /// <summary>The setting or the Last.fm connection changed. Before Home has
    /// loaded there is nothing to lay out; the first load places them.</summary>
    private void OnStationsChanged()
    {
        // The scrobbler reports every change (a play queued, a retry); only a
        // change in which stations show moves anything.
        var wanted = StationsWanted;
        if (wanted == _stationShelves.Count > 0)
            return;
        // Items without Shelves: Home failed and shows only the saved lists.
        if (Shelves.Count > 0 || Items.Count > 0)
            RebuildItems(freshStations: false);
        else if (_stations?.User is null)
            _stationShelves.ForEach(s => s.Cancel());
    }

    [RelayCommand]
    private Task OpenEntryAsync(CatalogEntry entry) => _navigation.OpenEntryAsync(entry);

    /// <summary>A station shelf's "See all".</summary>
    [RelayCommand]
    private void OpenStation(StationShelfViewModel station) => _navigation.OpenStation(station);

    /// <summary>A card's hover play button: plays without opening the page.</summary>
    [RelayCommand]
    private Task PlayEntryAsync(CatalogEntry entry) => _navigation.PlayEntryAsync(entry);
}
