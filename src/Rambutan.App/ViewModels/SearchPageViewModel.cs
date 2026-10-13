using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Services;
using Rambutan.Core.Catalog;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.App.ViewModels;

/// <summary>
/// One chip above the results. <see cref="Filter"/> null is "All": the unfiltered
/// search with YTM's top result. It stays an App notion because
/// Core's <see cref="SearchFilter"/> maps one to one onto YTM's filter params.
/// </summary>
public sealed record SearchTab(SearchFilter? Filter)
{
    public static SearchTab All { get; } = new((SearchFilter?)null);
}

/// <summary>A query from this machine's search history, in the drop-down.</summary>
public sealed record RecentQueryItem(string Query);

/// <summary>A completion YTM offered for the typed text; <see cref="FromHistory"/>
/// marks the signed-in account's own past searches.</summary>
public sealed record QuerySuggestionItem(string Query, bool FromHistory);

public partial class SearchPageViewModel : ViewModelBase
{
    private readonly ICatalog _catalog;
    private readonly PlaybackCoordinator _coordinator;
    private readonly NavigationService _navigation;
    private readonly SearchHistory _history;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private LoadError? _loadError;
    private SearchTab _selectedTab = SearchTab.All;

    /// <summary>The filter on screen; changing it searches again. Null (a chip list
    /// cleared by Ctrl+click) is ignored: one filter is always selected.</summary>
    public SearchTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is null || !SetProperty(ref _selectedTab, value))
                return;
            SearchCommand.Execute(null);
        }
    }
    /// <summary>Distinguishes "no results" from "hasn't searched yet" (empty states).</summary>
    [ObservableProperty] private bool _hasSearched;

    /// <summary>A filtered search has another page; the view asks for it as the
    /// list nears its end. Always false on All, which YTM doesn't page.</summary>
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _isLoadingMore;

    /// <summary>The next page failed. Shown under the results, which stay, with
    /// Try again; <see cref="LoadError"/> is for a search that has nothing to show.</summary>
    [ObservableProperty] private LoadError? _moreError;

    /// <summary>What the next page continues: the query and filter of the search
    /// on screen, and its token. Null when there is no next page.</summary>
    private (string Query, SearchFilter Filter, ContinuationToken Token)? _next;

    /// <summary>YTM's top-result card, on the All tab only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopIsSong), nameof(TopIsArtist))]
    private TopResult? _topResult;

    /// <summary>The card plays a song and opens anything else.</summary>
    public bool TopIsSong => TopResult?.Entry is SongEntry;

    /// <summary>Artists get round art, as in every other list.</summary>
    public bool TopIsArtist => TopResult?.Entry is ArtistEntry;

    public IReadOnlyList<SearchTab> Tabs { get; } =
        [SearchTab.All, new(SearchFilter.Songs), new(SearchFilter.Videos), new(SearchFilter.Albums),
         new(SearchFilter.Artists), new(SearchFilter.Playlists)];

    public ObservableCollection<CatalogEntry> Results { get; } = [];

    // Design-time
    public SearchPageViewModel() : this(null!, null!, null!)
    {
        Results.Add(new SongEntry
        {
            Title = "Instant Crush",
            Subtitle = "Daft Punk",
            Track = new TrackInfo
            {
                VideoId = "x", Title = "Instant Crush", Artists = [new("Daft Punk")],
                Album = "Random Access Memories", Duration = TimeSpan.FromMinutes(5),
            },
        });
        Results.Add(new AlbumEntry
        {
            Title = "Random Access Memories", Subtitle = "Album • Daft Punk • 2013", BrowseId = "x",
        });
    }

    public SearchPageViewModel(
        ICatalog catalog, PlaybackCoordinator coordinator, NavigationService navigation,
        SearchHistory? history = null)
    {
        _catalog = catalog;
        _coordinator = coordinator;
        _navigation = navigation;
        _history = history ?? new SearchHistory(null);
    }

    // With the box empty it lists recent queries; while typing, the recent ones
    // that match, then YTM's completions, then the entities YTM matched outright.

    /// <summary>Recent queries, YTM's completions and matched entities, in that order.</summary>
    public ObservableCollection<object> Dropdown { get; } = [];

    [ObservableProperty] private bool _isDropdownOpen;

    /// <summary>The box is empty and the list is the history, which gets a header
    /// with Clear.</summary>
    [ObservableProperty] private bool _showingRecent;

    internal const int RecentShownWhenEmpty = 8;
    internal const int RecentShownWhileTyping = 3;
    internal static readonly TimeSpan SuggestDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>The pause before asking YTM, so a fast typist sends one request,
    /// not one per key. Replaced in tests.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    private bool _fieldActive;
    private bool _settingQuery;
    private CancellationTokenSource? _suggest;

    /// <summary>The view reports whether the box (or the drop-down under it) has
    /// keyboard focus; the drop-down only shows while it does.</summary>
    public void SetFieldActive(bool active)
    {
        if (_fieldActive == active)
            return;
        _fieldActive = active;
        // Focus alone offers history only. With a query already in the box (back
        // on Search, or after a search) the list waits for a keystroke instead of
        // covering the results.
        if (active && string.IsNullOrWhiteSpace(Query))
            RefreshDropdown();
        else if (!active)
            CloseDropdown();
    }

    /// <summary>Escape, a search, or a pick: hide the list until the next keystroke.</summary>
    public void CloseDropdown()
    {
        _suggest?.Cancel();
        _suggest = null;
        IsDropdownOpen = false;
    }

    partial void OnQueryChanged(string value)
    {
        if (!_settingQuery)
            RefreshDropdown();
    }

    private void RefreshDropdown()
    {
        _suggest?.Cancel();
        _suggest = null;
        if (!_fieldActive || _catalog is null)
            return;

        var text = Query.Trim();
        if (text.Length == 0)
        {
            ShowingRecent = true;
            Fill(_history.Queries.Take(RecentShownWhenEmpty).Select(q => new RecentQueryItem(q)));
            return;
        }

        // The list on screen stays until the new one is ready, so typing doesn't
        // make it flicker empty between keys.
        var suggest = new CancellationTokenSource();
        _suggest = suggest;
        _ = SuggestAsync(text, suggest);
    }

    private async Task SuggestAsync(string text, CancellationTokenSource suggest)
    {
        var recent = RecentMatching(text).ToList();
        SearchSuggestions remote;
        try
        {
            await Delay(SuggestDelay, suggest.Token);
            remote = await _catalog.GetSearchSuggestionsAsync(text, suggest.Token);
        }
        catch (Exception ex)
        {
            if (suggest.IsCancellationRequested)
                return;
            // Suggestions are a convenience: the box still searches, so the list
            // keeps only what this machine knows.
            AppLog.Write("search", $"suggestions for the typed text failed: {ex.Message}");
            remote = SearchSuggestions.Empty;
        }

        if (suggest.IsCancellationRequested)
            return;

        ShowingRecent = false;
        var shown = recent.Select(r => r.Query).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Fill(recent.Cast<object>()
            .Concat(remote.Queries.Where(q => !shown.Contains(q.Query))
                .Select(q => new QuerySuggestionItem(q.Query, q.FromHistory)))
            .Concat(remote.Entries));
    }

    private IEnumerable<RecentQueryItem> RecentMatching(string text) =>
        _history.Queries
            .Where(q => q.StartsWith(text, StringComparison.CurrentCultureIgnoreCase) &&
                        !string.Equals(q, text, StringComparison.CurrentCultureIgnoreCase))
            .Take(RecentShownWhileTyping)
            .Select(q => new RecentQueryItem(q));

    private void Fill(IEnumerable<object> items)
    {
        Dropdown.Clear();
        foreach (var item in items)
            Dropdown.Add(item);
        IsDropdownOpen = _fieldActive && Dropdown.Count > 0;
    }

    /// <summary>A query runs as a search; a song plays on its own (autoplay carries
    /// on from it); anything else opens.</summary>
    [RelayCommand]
    private async Task PickAsync(object? item)
    {
        CloseDropdown();
        switch (item)
        {
            case RecentQueryItem recent:
                RunQuery(recent.Query);
                break;
            case QuerySuggestionItem suggestion:
                RunQuery(suggestion.Query);
                break;
            case SongEntry song:
                await _coordinator.PlayQueueAsync([song.Track], 0);
                break;
            case CatalogEntry entry:
                await _navigation.OpenEntryAsync(entry);
                break;
        }
    }

    /// <summary>
    /// Runs a search a rambutan:// link asked for. It isn't added to the
    /// search history: a web page can fire such a link, and the history persists.
    /// </summary>
    public void RunFromLink(string query)
    {
        _forgetNext = true;
        RunQuery(query);
    }

    /// <summary>Set for one search that the history must not keep.</summary>
    private bool _forgetNext;

    private void RunQuery(string query)
    {
        _settingQuery = true;
        try
        {
            Query = query;
        }
        finally
        {
            _settingQuery = false;
        }

        SearchCommand.Execute(null);
    }

    [RelayCommand]
    private void RemoveRecent(string query)
    {
        _history.Remove(query);
        var shown = Dropdown.OfType<RecentQueryItem>().FirstOrDefault(r => r.Query == query);
        if (shown is not null)
            Dropdown.Remove(shown);
        if (ShowingRecent)
            RefreshDropdown(); // the next one moves up into the list
        IsDropdownOpen = _fieldActive && Dropdown.Count > 0;
    }

    [RelayCommand]
    private void ClearRecent()
    {
        _history.Clear();
        RefreshDropdown();
        IsDropdownOpen = _fieldActive && Dropdown.Count > 0;
    }

    /// <summary>The fetch whose results the page will show; any earlier one is stale.</summary>
    private CancellationTokenSource? _search;

    /// <summary>Raised when the page should take keyboard focus (e.g. Ctrl+F while
    /// already on Search, where no new view is created to focus itself).</summary>
    public event Action? FocusRequested;

    public void RequestFocus() => FocusRequested?.Invoke();

    /// <summary>
    /// Latest wins: each search cancels the one before it, and only the newest may
    /// write results or clear the busy flag (fetches finish in any order). Concurrent
    /// executions are allowed so Enter and filter clicks are never swallowed while a
    /// fetch is in flight; superseding is this method's job, not the command's.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SearchAsync()
    {
        _search?.Cancel();
        _search = null;
        // A page still on its way belongs to the search this one replaces.
        _more?.Cancel();
        _more = null;
        _next = null;
        HasMore = false;
        IsLoadingMore = false;
        MoreError = null;

        if (_catalog is null)
            return;

        // An empty box means no search, not "the last search under a new filter":
        // back to the page's resting state.
        if (string.IsNullOrWhiteSpace(Query))
        {
            Results.Clear();
            TopResult = null;
            HasSearched = false;
            IsBusy = false;
            LoadError = null;
            return;
        }

        CloseDropdown();
        if (!_forgetNext)
            _history.Add(Query);
        _forgetNext = false;

        var search = new CancellationTokenSource();
        _search = search;
        IsBusy = true;
        HasSearched = true;
        LoadError = null;
        try
        {
            var query = Query.Trim();
            TopResult? top = null;
            IReadOnlyList<CatalogEntry> entries;
            (string, SearchFilter, ContinuationToken)? next = null;
            if (SelectedTab.Filter is { } filter)
            {
                var page = await _catalog.SearchPagedAsync(query, filter, search.Token);
                entries = page.Items;
                if (page.Continuation is { } token)
                    next = (query, filter, token);
            }
            else
            {
                var overview = await _catalog.SearchAllAsync(query, search.Token);
                (top, entries) = (overview.TopResult, overview.Results);
            }

            if (search.IsCancellationRequested)
                return;

            TopResult = top;
            Results.Clear();
            foreach (var entry in entries)
                Results.Add(entry);
            _next = next;
            HasMore = next is not null;
        }
        catch (Exception ex)
        {
            if (!search.IsCancellationRequested)
            {
                // A failed search must not leave the previous query's results
                // standing under the new query and filter.
                Results.Clear();
                TopResult = null;
                LoadError = LoadError.From(ex, LoadSubject.SearchResults, SearchCommand);
            }
        }
        finally
        {
            // Not disposed: a superseded search can finish inside the newer one's
            // Cancel() call, and disposing a source from its own cancellation
            // callback deadlocks. It has no timer, so the GC owes it nothing.
            if (_search == search)
            {
                _search = null;
                IsBusy = false;
            }
        }
    }

    /// <summary>The page fetch in flight; a new search cancels it.</summary>
    private CancellationTokenSource? _more;

    /// <summary>
    /// The next page of the filtered search on screen, appended to its results.
    /// A no-op while a page or a search is on its way, or when there are no more.
    /// A page that arrives after a new search has started is dropped.
    /// </summary>
    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (_next is not { } next || _more is not null || _search is not null || _catalog is null)
            return;

        var more = new CancellationTokenSource();
        _more = more;
        IsLoadingMore = true;
        MoreError = null;
        try
        {
            var page = await _catalog.GetSearchContinuationAsync(next.Query, next.Filter, next.Token, more.Token);
            if (more.IsCancellationRequested)
                return;

            // A row YTM repeats across pages would be the same row twice.
            var shown = Results.Select(Key).ToHashSet();
            foreach (var entry in page.Items)
            {
                if (shown.Add(Key(entry)))
                    Results.Add(entry);
            }

            _next = page.Continuation is { } token && token != next.Token && page.Items.Count > 0
                ? next with { Token = token }
                : null;
            HasMore = _next is not null;
        }
        catch (Exception ex)
        {
            if (!more.IsCancellationRequested)
                MoreError = LoadError.From(ex, LoadSubject.MoreResults, LoadMoreCommand);
        }
        finally
        {
            if (_more == more)
            {
                _more = null;
                IsLoadingMore = false;
            }
        }
    }

    /// <summary>What makes two result rows the same row.</summary>
    private static string Key(CatalogEntry entry) => entry switch
    {
        SongEntry song => "song:" + song.Track.VideoId,
        AlbumEntry album => "album:" + album.BrowseId,
        ArtistEntry artist => "artist:" + artist.BrowseId,
        PlaylistEntry playlist => "playlist:" + playlist.PlaylistId,
        _ => entry.GetType().Name + ":" + entry.Title,
    };

    /// <summary>Songs play with the list they came from as their queue; the rest navigate.</summary>
    [RelayCommand]
    private async Task OpenAsync(CatalogEntry entry)
    {
        if (entry is SongEntry song)
            await PlayAsync(song);
        else
            await _navigation.OpenEntryAsync(entry);
    }

    [RelayCommand]
    private async Task PlayAsync(SongEntry song)
    {
        var (tracks, index) = QueueFor(song);
        if (index >= 0)
            await _coordinator.PlayQueueAsync(tracks, index);
    }

    /// <summary>
    /// The queue a song plays in: the rows of the list it sits in. A song inside
    /// the top-result card plays with the card's other songs, as YTM does; the
    /// card's own song leads the results.
    /// </summary>
    internal (List<TrackInfo> Tracks, int Index) QueueFor(SongEntry song)
    {
        IEnumerable<CatalogEntry> list = Results;
        if (TopResult is { } top)
        {
            if (top.Related.OfType<SongEntry>().Any(e => e.Track.VideoId == song.Track.VideoId))
                list = top.Related;
            else if (top.Entry is SongEntry lead && lead.Track.VideoId == song.Track.VideoId)
                list = Results.Prepend(lead);
        }

        var tracks = list.OfType<SongEntry>().Select(e => e.Track)
            .DistinctBy(t => t.VideoId).ToList();
        return (tracks, tracks.FindIndex(t => t.VideoId == song.Track.VideoId));
    }

    /// <summary>The row "+": the shared Add to queue, so it toasts like the menu's.
    /// With nothing playing, it also starts.</summary>
    [RelayCommand]
    private async Task EnqueueAsync(SongEntry song)
    {
        if (_navigation?.Actions is { } actions)
            actions.AddToQueueCommand.Execute(song.Track);
        else
            _coordinator.Queue.Enqueue(song.Track);
        if (_coordinator.State is PlaybackState.Stopped)
            await _coordinator.JumpToAsync(_coordinator.Queue.CurrentIndex);
    }
}
