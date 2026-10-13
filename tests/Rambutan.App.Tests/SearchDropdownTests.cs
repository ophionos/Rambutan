using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>The list under the search box: recent queries with
/// the box empty; matching recent ones, YTM's completions and entities while typing.</summary>
public class SearchDropdownTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly SearchHistory _history = new(null);

    private SearchPageViewModel CreateVm(params string[] recent)
    {
        foreach (var query in recent.Reverse())
            _history.Add(query);
        // No pause before asking: the fake answers when the test says so.
        return new SearchPageViewModel(_catalog, null!, null!, _history)
        {
            Delay = (_, _) => Task.CompletedTask,
        };
    }

    /// <summary>No SynchronizationContext, so a completed fake runs the VM's
    /// continuation inline (see SearchPageViewModelTests.Inline).</summary>
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

    [Fact]
    public void FocusOnAnEmptyBox_ListsRecentQueries()
    {
        Inline(() =>
        {
            var vm = CreateVm("justice", "daft punk");
            vm.SetFieldActive(true);

            Assert.True(vm.IsDropdownOpen);
            Assert.True(vm.ShowingRecent);
            Assert.Equal([new RecentQueryItem("justice"), new RecentQueryItem("daft punk")], vm.Dropdown);
            Assert.Empty(_catalog.Suggestions);
        });
    }

    [Fact]
    public void FocusOnAnEmptyBox_WithNoHistory_OpensNothing()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            vm.SetFieldActive(true);

            Assert.False(vm.IsDropdownOpen);
        });
    }

    [Fact]
    public void FocusWithAQueryInTheBox_WaitsForAKeystroke()
    {
        // Back on Search with results showing: the list must not cover them.
        Inline(() =>
        {
            var vm = CreateVm("daft punk");
            vm.Query = "daft punk";
            vm.SetFieldActive(true);

            Assert.False(vm.IsDropdownOpen);
            Assert.Empty(_catalog.Suggestions);
        });
    }

    [Fact]
    public void Typing_ListsMatchingRecent_ThenCompletions_ThenEntries()
    {
        Inline(() =>
        {
            var vm = CreateVm("daft punk", "justice");
            vm.SetFieldActive(true);
            vm.Query = "da";

            var album = new AlbumEntry { Title = "Discovery", BrowseId = "MPRE" };
            Assert.Equal("da", Assert.Single(_catalog.Suggestions).Query);
            _catalog.Suggestions[0].Complete(["daft punk", "daft punk live"], album);

            Assert.False(vm.ShowingRecent);
            Assert.Equal<object>(
                [new RecentQueryItem("daft punk"), new QuerySuggestionItem("daft punk live", false), album],
                vm.Dropdown);
            Assert.True(vm.IsDropdownOpen);
        });
    }

    [Fact]
    public void OnlyTheNewestKeystroke_FillsTheList()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            vm.SetFieldActive(true);
            vm.Query = "d";
            vm.Query = "da";

            var (first, second) = (_catalog.Suggestions[0], _catalog.Suggestions[1]);
            Assert.True(first.Token.IsCancellationRequested);
            second.Complete(["daft punk"]);
            first.Complete(["drake"]); // late

            Assert.Equal([new QuerySuggestionItem("daft punk", false)], vm.Dropdown);
        });
    }

    [Fact]
    public void FailedSuggestions_StillListMatchingRecentQueries()
    {
        Inline(() =>
        {
            var vm = CreateVm("daft punk");
            vm.SetFieldActive(true);
            vm.Query = "da";
            _catalog.Suggestions[0].Result.TrySetException(new HttpRequestException("offline"));

            Assert.Equal([new RecentQueryItem("daft punk")], vm.Dropdown);
        });
    }

    [Fact]
    public void PickingACompletion_SearchesForIt_AndRecordsIt()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            vm.SetFieldActive(true);
            vm.Query = "da";
            _catalog.Suggestions[0].Complete(["daft punk live"]);

            vm.PickCommand.Execute(vm.Dropdown[0]);

            Assert.Equal("daft punk live", vm.Query);
            Assert.False(vm.IsDropdownOpen);
            Assert.Equal("daft punk live", Assert.Single(_catalog.Searches).Query);
            Assert.Single(_catalog.Suggestions); // setting the box asked for nothing more
            Assert.Equal(["daft punk live"], _history.Queries);
        });
    }

    [Fact]
    public void Searching_ClosesTheList_AndRecordsTheQuery()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            vm.SetFieldActive(true);
            vm.Query = "justice";
            vm.SearchCommand.Execute(null);

            Assert.False(vm.IsDropdownOpen);
            Assert.True(_catalog.Suggestions[0].Token.IsCancellationRequested);
            Assert.Equal(["justice"], _history.Queries);
        });
    }

    [Fact]
    public void RemovingARecentQuery_TakesItOffTheList()
    {
        Inline(() =>
        {
            var vm = CreateVm("justice", "daft punk");
            vm.SetFieldActive(true);

            vm.RemoveRecentCommand.Execute("justice");

            Assert.Equal([new RecentQueryItem("daft punk")], vm.Dropdown);
            Assert.Equal(["daft punk"], _history.Queries);
        });
    }

    [Fact]
    public void ClearingTheHistory_ClosesTheList()
    {
        Inline(() =>
        {
            var vm = CreateVm("justice", "daft punk");
            vm.SetFieldActive(true);

            vm.ClearRecentCommand.Execute(null);

            Assert.Empty(_history.Queries);
            Assert.False(vm.IsDropdownOpen);
        });
    }

    [Fact]
    public void LeavingTheBox_ClosesTheList()
    {
        Inline(() =>
        {
            var vm = CreateVm("justice");
            vm.SetFieldActive(true);
            vm.SetFieldActive(false);

            Assert.False(vm.IsDropdownOpen);
        });
    }
}
