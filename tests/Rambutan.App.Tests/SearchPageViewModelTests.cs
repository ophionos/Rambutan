using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

public class SearchPageViewModelTests
{
    private readonly FakeCatalog _catalog = new();

    // Search never touches playback or navigation; only Open/Play do.
    private SearchPageViewModel CreateVm() => new(_catalog, null!, null!);

    /// <summary>
    /// Runs a test body with no SynchronizationContext. xunit installs its own,
    /// which posts continuations to worker threads (so asserts would race them) and
    /// waits for every async void to finish (AsyncRelayCommand.Execute is one, so a
    /// search a test leaves pending hangs the run). Without it, completing a fake
    /// fetch runs the VM's continuation inline and the test reads settled state.
    /// </summary>
    private static void Inline(Action body)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            body();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static string[] Titles(SearchPageViewModel vm) => vm.Results.Select(r => r.Title).ToArray();

    [Fact]
    public void SwitchingFilters_ShowsTheSelectedFilter_WhateverOrderFetchesFinish()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            vm.Query = "daft punk";
            vm.SelectedTab = new(SearchFilter.Albums);
            vm.SelectedTab = new(SearchFilter.Artists);

            var (albums, artists) = (_catalog.Searches[0], _catalog.Searches[1]);
            Assert.True(albums.Token.IsCancellationRequested);

            artists.Complete("Daft Punk");
            albums.Complete("Random Access Memories"); // late, and already superseded

            Assert.Equal(["Daft Punk"], Titles(vm));
            Assert.False(vm.IsBusy);
        });
    }

    [Fact]
    public void BusyStaysOn_UntilTheNewestSearchFinishes()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            vm.Query = "daft punk";
            vm.SelectedTab = new(SearchFilter.Albums);
            vm.SelectedTab = new(SearchFilter.Artists);

            // The superseded fetch settling must not stop the spinner.
            _catalog.Searches[0].Complete("Random Access Memories");
            Assert.True(vm.IsBusy);

            _catalog.Searches[1].Complete("Daft Punk");
            Assert.False(vm.IsBusy);
        });
    }

    [Fact]
    public void AFailedSupersededSearch_ShowsNoError()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            vm.Query = "daft punk";
            vm.SelectedTab = new(SearchFilter.Albums);
            vm.SelectedTab = new(SearchFilter.Artists);

            _catalog.Searches[0].Fail("boom");
            _catalog.Searches[1].Complete("Daft Punk");

            Assert.Null(vm.LoadError);
            Assert.Equal(["Daft Punk"], Titles(vm));
        });
    }

    [Fact]
    public void ChangingFilter_WithAnEmptyBox_ClearsOldResults()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            vm.Query = "daft punk";
            vm.SearchCommand.Execute(null);
            _catalog.Searches[0].Complete("One More Time");

            vm.Query = "";
            vm.SelectedTab = new(SearchFilter.Albums);

            Assert.Empty(vm.Results);
            Assert.False(vm.HasSearched); // back to "Search YouTube Music", not "No results"
            Assert.Single(_catalog.Searches); // and no fetch for an empty query
        });
    }

    [Fact]
    public void AFailedSearch_DoesNotLeaveThePreviousResultsStanding()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            vm.Query = "daft punk";
            vm.SearchCommand.Execute(null);
            _catalog.Searches[0].Complete("One More Time");

            vm.Query = "justice";
            vm.SearchCommand.Execute(null);
            _catalog.Searches[1].Fail("offline");

            Assert.Empty(vm.Results);
            Assert.Equal("Can't reach YouTube Music", vm.LoadError!.Title);
            Assert.Same(vm.SearchCommand, vm.LoadError.Retry);
            Assert.False(vm.IsBusy);
        });
    }

    [Fact]
    public void Search_RunsWhileAnotherIsInFlight()
    {
        Inline(() =>
        {
            // The Search button must not grey out, nor Enter be swallowed, because
            // a slow fetch is still running.
            var vm = CreateVm();
            vm.Query = "daft";
            vm.SearchCommand.Execute(null);
            Assert.True(vm.SearchCommand.CanExecute(null));
            vm.Query = "daft punk";
            vm.SearchCommand.Execute(null);

            Assert.Equal(2, _catalog.Searches.Count);
            Assert.Equal("daft punk", _catalog.Searches[1].Query);
            _catalog.Searches[1].Complete("One More Time");
        });
    }

    private static SongEntry Song(string id) => new()
    {
        Title = id,
        Track = new TrackInfo { VideoId = id, Title = id, Artists = [] },
    };

    private static TopResult Top(CatalogEntry entry, params CatalogEntry[] related) =>
        new() { Entry = entry, Related = related };

    [Fact]
    public void ByDefault_SearchesAll_AndShowsTheTopResult()
    {
        // "daft punk" used to land on a song list.
        Inline(() =>
        {
            var vm = CreateVm();
            vm.Query = "daft punk";
            vm.SearchCommand.Execute(null);

            var call = Assert.Single(_catalog.Searches);
            Assert.Null(call.Filter);
            call.Top = Top(new ArtistEntry { Title = "Daft Punk", BrowseId = "UC1" }, Song("a"));
            call.Complete("Discovery");

            Assert.Equal("Daft Punk", vm.TopResult!.Entry.Title);
            Assert.True(vm.TopIsArtist);
            Assert.False(vm.TopIsSong);
            Assert.Equal(["Discovery"], Titles(vm));
        });
    }

    [Fact]
    public void AFilteredTab_ShowsNoTopResult_EvenWhenAllFinishesLate()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            vm.Query = "daft punk";
            vm.SearchCommand.Execute(null);
            vm.SelectedTab = new(SearchFilter.Songs);

            var (all, songs) = (_catalog.Searches[0], _catalog.Searches[1]);
            Assert.True(all.Token.IsCancellationRequested);
            songs.Complete("One More Time");
            all.Top = Top(new ArtistEntry { Title = "Daft Punk", BrowseId = "UC1" });
            all.Complete("Discovery"); // superseded

            Assert.Null(vm.TopResult);
            Assert.Equal(["One More Time"], Titles(vm));
        });
    }

    [Fact]
    public void ReturningToAll_AfterAFilter_SearchesAllAgain()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            vm.Query = "daft punk";
            vm.SelectedTab = new(SearchFilter.Albums);
            vm.SelectedTab = SearchTab.All;

            Assert.Equal([SearchFilter.Albums, null], _catalog.Searches.Select(s => s.Filter));
        });
    }

    [Fact]
    public void ClearingTheChips_KeepsTheFilterAndDoesNotSearch()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            vm.Query = "daft punk";
            vm.SelectedTab = new(SearchFilter.Albums);

            vm.SelectedTab = null!;

            Assert.Equal(new SearchTab(SearchFilter.Albums), vm.SelectedTab);
            Assert.Equal([SearchFilter.Albums], _catalog.Searches.Select(s => s.Filter));
        });
    }

    [Fact]
    public void AFailedAllSearch_ClearsTheTopResult()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            vm.Query = "daft punk";
            vm.SearchCommand.Execute(null);
            _catalog.Searches[0].Top = Top(Song("lead"));
            _catalog.Searches[0].Complete("Discovery");

            vm.SearchCommand.Execute(null);
            _catalog.Searches[1].Fail("offline");

            Assert.Null(vm.TopResult);
            Assert.NotNull(vm.LoadError);
        });
    }

    [Fact]
    public void ASongInTheCard_QueuesWithTheCardsSongs()
    {
        var vm = CreateVm();
        vm.TopResult = Top(new ArtistEntry { Title = "Daft Punk", BrowseId = "UC1" }, Song("r1"), Song("r2"));
        vm.Results.Add(Song("x"));

        var (tracks, index) = vm.QueueFor(Song("r2"));

        Assert.Equal(["r1", "r2"], tracks.Select(t => t.VideoId));
        Assert.Equal(1, index);
    }

    [Fact]
    public void TheCardsOwnSong_LeadsTheResults()
    {
        var vm = CreateVm();
        vm.TopResult = Top(Song("lead"));
        vm.Results.Add(Song("x"));
        vm.Results.Add(Song("lead")); // YTM can list it again below; play it once

        var (tracks, index) = vm.QueueFor(Song("lead"));

        Assert.Equal(["lead", "x"], tracks.Select(t => t.VideoId));
        Assert.Equal(0, index);
    }

    [Fact]
    public void AResultSong_QueuesWithTheResults()
    {
        var vm = CreateVm();
        vm.TopResult = Top(new ArtistEntry { Title = "Daft Punk", BrowseId = "UC1" }, Song("r1"));
        vm.Results.Add(new AlbumEntry { Title = "Discovery", BrowseId = "MPRE" });
        vm.Results.Add(Song("x"));
        vm.Results.Add(Song("y"));

        var (tracks, index) = vm.QueueFor(Song("y"));

        Assert.Equal(["x", "y"], tracks.Select(t => t.VideoId));
        Assert.Equal(1, index);
    }

    private SearchPageViewModel SearchSongs(string query = "daft punk")
    {
        var vm = CreateVm();
        vm.Query = query;
        vm.SelectedTab = new(SearchFilter.Songs);
        return vm;
    }

    [Fact]
    public void AFilteredSearch_LoadsItsNextPage_OntoTheEnd()
    {
        Inline(() =>
        {
            var vm = SearchSongs();
            _catalog.Searches[0].CompleteWithMore("page2", "One", "Two");
            Assert.True(vm.HasMore);

            vm.LoadMoreCommand.Execute(null);
            var next = _catalog.Searches[1];
            Assert.Equal(("daft punk", SearchFilter.Songs, "page2"), (next.Query, next.Filter, next.From?.Value));
            Assert.True(vm.IsLoadingMore);

            next.Complete("Three");

            Assert.Equal(["One", "Two", "Three"], Titles(vm));
            Assert.False(vm.HasMore);
            Assert.False(vm.IsLoadingMore);
        });
    }

    [Fact]
    public void TheAllTab_HasNoNextPage()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            vm.Query = "daft punk";
            vm.SearchCommand.Execute(null);
            _catalog.Searches[0].Complete("One");

            Assert.False(vm.HasMore);
            vm.LoadMoreCommand.Execute(null);
            Assert.Single(_catalog.Searches);
        });
    }

    [Fact]
    public void AskingAgainWhileAPageIsOnItsWay_AsksOnce()
    {
        Inline(() =>
        {
            var vm = SearchSongs();
            _catalog.Searches[0].CompleteWithMore("page2", "One");

            vm.LoadMoreCommand.Execute(null);
            vm.LoadMoreCommand.Execute(null);

            Assert.Equal(2, _catalog.Searches.Count);
        });
    }

    [Fact]
    public void APageThatArrivesAfterANewSearch_IsDropped()
    {
        Inline(() =>
        {
            var vm = SearchSongs();
            _catalog.Searches[0].CompleteWithMore("page2", "One");
            vm.LoadMoreCommand.Execute(null);
            var stale = _catalog.Searches[1];

            vm.SelectedTab = new(SearchFilter.Albums);
            Assert.True(stale.Token.IsCancellationRequested);
            Assert.False(vm.IsLoadingMore);
            _catalog.Searches[2].Complete("Discovery");
            stale.Complete("Late song");

            Assert.Equal(["Discovery"], Titles(vm));
            Assert.False(vm.HasMore);
        });
    }

    [Fact]
    public void AFailedPage_KeepsTheResults_AndCanBeTriedAgain()
    {
        Inline(() =>
        {
            var vm = SearchSongs();
            _catalog.Searches[0].CompleteWithMore("page2", "One");

            vm.LoadMoreCommand.Execute(null);
            _catalog.Searches[1].Fail("connection reset");

            Assert.Equal(["One"], Titles(vm));
            Assert.NotNull(vm.MoreError);
            Assert.Null(vm.LoadError);
            Assert.True(vm.HasMore);

            vm.MoreError!.Retry!.Execute(null);
            Assert.Null(vm.MoreError);
            Assert.Equal("page2", _catalog.Searches[2].From?.Value);
            _catalog.Searches[2].Complete("Two");
            Assert.Equal(["One", "Two"], Titles(vm));
        });
    }

    [Fact]
    public void RowsAPageRepeats_AreNotShownTwice_AndARepeatedTokenEndsPaging()
    {
        Inline(() =>
        {
            var vm = SearchSongs();
            _catalog.Searches[0].CompleteWithMore("page2", "One", "Two");

            vm.LoadMoreCommand.Execute(null);
            _catalog.Searches[1].CompleteWithMore("page2", "Two", "Three");

            Assert.Equal(["One", "Two", "Three"], Titles(vm));
            Assert.False(vm.HasMore);
        });
    }
}
