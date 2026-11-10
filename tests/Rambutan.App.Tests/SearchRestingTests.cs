using System.Net.Http;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>The Search page before anything is searched: recent searches and
/// Moods &amp; genres, and how typing and clearing the box swap them.</summary>
public class SearchRestingTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly SearchHistory _history = new(null);

    private static readonly MoodsPage OneMood = new()
    {
        Sections = [new MoodSection("Genres", [new MoodCategory("Jazz", "JAZZ")])],
    };

    private SearchPageViewModel CreateVm(bool withMoods, params string[] recent)
    {
        foreach (var query in recent.Reverse())
            _history.Add(query);
        var moods = withMoods ? new MoodsPageViewModel(_catalog, null!) : null;
        return new SearchPageViewModel(_catalog, null!, null!, _history, moods: moods);
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

    private static string[] Queries(SearchPageViewModel vm) => vm.Recent.Select(r => r.Query).ToArray();

    [Fact]
    public void TheRecentList_MirrorsTheHistory_AndFollowsRemoveClearAndANewSearch()
    {
        Inline(() =>
        {
            var vm = CreateVm(false, "justice", "daft punk", "air");
            Assert.Equal(["justice", "daft punk", "air"], Queries(vm));
            Assert.True(vm.HasRecent);

            vm.RemoveRecentCommand.Execute("daft punk");
            Assert.Equal(["justice", "air"], Queries(vm));

            vm.Query = "moby";
            vm.SearchCommand.Execute(null);
            Assert.Equal(["moby", "justice", "air"], Queries(vm));

            vm.ClearRecentCommand.Execute(null);
            Assert.Empty(vm.Recent);
            Assert.False(vm.HasRecent);
        });
    }

    [Fact]
    public void RunningAChip_Searches_AndMovesTheQueryToTheFront()
    {
        Inline(() =>
        {
            var vm = CreateVm(false, "justice", "daft punk");

            vm.PickCommand.Execute(vm.Recent[1]);

            Assert.Equal("daft punk", vm.Query);
            Assert.Equal("daft punk", Assert.Single(_catalog.Searches).Query);
            Assert.Equal(["daft punk", "justice"], Queries(vm));
            Assert.True(vm.HasSearched);
        });
    }

    [Fact]
    public void ClearingTheBox_ReturnsToTheRestingPage_AndALateAnswerDoesNotShow()
    {
        Inline(() =>
        {
            var vm = CreateVm(false, "justice");
            vm.Query = "daft punk";
            vm.SearchCommand.Execute(null);
            var search = Assert.Single(_catalog.Searches);
            Assert.True(vm.HasSearched);
            Assert.True(vm.IsBusy);

            vm.Query = "";

            Assert.False(vm.HasSearched);
            Assert.False(vm.IsBusy);
            Assert.True(search.Token.IsCancellationRequested);
            search.Complete("Discovery"); // late
            Assert.Empty(vm.Results);
            Assert.False(vm.HasSearched);
        });
    }

    [Fact]
    public void ClearingTheBox_AfterResults_RemovesThem()
    {
        Inline(() =>
        {
            var vm = CreateVm(false);
            vm.Query = "daft punk";
            vm.SearchCommand.Execute(null);
            _catalog.Searches[0].Complete("Discovery");
            Assert.Single(vm.Results);

            vm.Query = "";

            Assert.Empty(vm.Results);
            Assert.False(vm.HasSearched);
            Assert.Null(vm.LoadError);
        });
    }

    [Fact]
    public async Task TheMoods_LoadOnce_AcrossTwoVisits()
    {
        var loads = 0;
        _catalog.Moods = () =>
        {
            loads++;
            return Task.FromResult(OneMood);
        };
        var vm = CreateVm(true);

        vm.EnsureMoodsLoaded();
        vm.EnsureMoodsLoaded();
        await Task.Yield();

        Assert.Equal(1, loads);
        Assert.Equal("Genres", Assert.Single(vm.Moods!.Sections).Title);
        Assert.True(vm.ShowsMoodsSection);
    }

    [Fact]
    public async Task FailedMoods_KeepTheRecentList_ExposeTheError_AndRetryLoadsThem()
    {
        _catalog.Moods = () => Task.FromException<MoodsPage>(new HttpRequestException("offline"));
        var vm = CreateVm(true, "justice");

        await vm.Moods!.LoadAsync();

        Assert.NotNull(vm.Moods.LoadError);
        Assert.Equal(["justice"], Queries(vm));
        Assert.False(vm.ShowsMoodsSection);
        Assert.False(vm.ShowsEmptyHint);

        _catalog.Moods = () => Task.FromResult(OneMood);
        await vm.Moods.ReloadCommand.ExecuteAsync(null);

        Assert.Null(vm.Moods.LoadError);
        Assert.True(vm.ShowsMoodsSection);
    }

    [Fact]
    public async Task WithNothingToShow_TheOldSentenceStays()
    {
        _catalog.Moods = () => Task.FromResult(MoodsPage.Empty);
        var vm = CreateVm(true);
        Assert.False(vm.ShowsEmptyHint); // the tiles are still on their way

        await vm.Moods!.LoadAsync();
        Assert.True(vm.ShowsEmptyHint);

        _history.Add("justice");
        Assert.False(vm.ShowsEmptyHint);
        _history.Clear();
        Assert.True(vm.ShowsEmptyHint);

        _catalog.Moods = () => Task.FromException<MoodsPage>(new HttpRequestException("offline"));
        await vm.Moods.LoadAsync();
        Assert.True(vm.ShowsEmptyHint); // with the error under it
    }

    [Fact]
    public void ASearchLink_LeavesTheRecentListAsItWas()
    {
        Inline(() =>
        {
            var vm = CreateVm(false, "justice");

            vm.RunFromLink("daft punk");

            Assert.Equal("daft punk", Assert.Single(_catalog.Searches).Query);
            Assert.Equal(["justice"], Queries(vm));
        });
    }

    [Fact]
    public void TheRestingPage_NamesItsRecentList_AndEachRemoveButton() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        _catalog.Moods = () => Task.FromResult(OneMood);
        var vm = CreateVm(true, "justice", "daft punk");
        vm.EnsureMoodsLoaded();
        var view = new Rambutan.App.Views.SearchPage { DataContext = vm };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var list = view.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "RecentList");
        Assert.True(list.IsEffectivelyVisible);
        Assert.Equal("Recent searches", AutomationProperties.GetName(list));
        var removers = list.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("recentremove")).ToArray();
        Assert.Equal(
            ["Remove justice from recent searches", "Remove daft punk from recent searches"],
            removers.Select(AutomationProperties.GetName).ToArray());
        Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("moodtile"));
        window.Close();
    });

    [Fact]
    public void TheRestingPage_WithNoHistory_ShowsNoRecentSection() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        _catalog.Moods = () => Task.FromResult(OneMood);
        var vm = CreateVm(true);
        vm.EnsureMoodsLoaded();
        var view = new Rambutan.App.Views.SearchPage { DataContext = vm };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var list = view.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "RecentList");
        Assert.False(list.IsEffectivelyVisible);
        window.Close();
    });
}
