using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>Search's On this computer shelf: what it shows, and that it stands
/// apart from YouTube Music's results.</summary>
public class SearchLocalShelfTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly List<LocalCall> _local = [];
    private readonly QueueModel _queue = new();

    private sealed record LocalCall(
        string Query, int Limit, CancellationToken Token,
        TaskCompletionSource<(IReadOnlyList<LocalTrackRow>, int)> Result)
    {
        public void Complete(int total, params string[] titles) =>
            Result.TrySetResult((titles.Select(Row).ToList(), total));
    }

    private SearchPageViewModel CreateVm() =>
        new(_catalog, FakePlayback.Coordinator(_queue), null!, searchLocal: (query, limit, ct) =>
        {
            var tcs = new TaskCompletionSource<(IReadOnlyList<LocalTrackRow>, int)>();
            ct.Register(() => tcs.TrySetCanceled(ct));
            _local.Add(new LocalCall(query, limit, ct, tcs));
            return tcs.Task;
        });

    private static int _next;

    private static LocalTrackRow Row(string title)
    {
        var id = $"local:{Interlocked.Increment(ref _next)}";
        return new(new LocalFile(id, $"/m/{id}.flac", 1, DateTimeOffset.UnixEpoch, new LocalFileTags("flac", Title: title)),
            new TrackInfo { VideoId = id, Title = title, Artists = [] });
    }

    /// <summary>No SynchronizationContext, so completing a fake runs the view
    /// model's continuation inline (see <see cref="SearchPageViewModelTests"/>).</summary>
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

    private static string[] Shelf(SearchPageViewModel vm) => vm.LocalResults.Select(r => r.Title).ToArray();

    private void Search(SearchPageViewModel vm, string query)
    {
        vm.Query = query;
        vm.SearchCommand.Execute(null);
    }

    [Fact]
    public void TheShelf_ShowsAFew_ThenMore_AndSaysWhenItStopsShort()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            Search(vm, "blue");
            var titles = Enumerable.Range(1, 8).Select(n => $"Blue {n}").ToArray();
            _local[0].Complete(120, titles);

            Assert.Equal(SearchPageViewModel.LocalKept, _local[0].Limit);
            Assert.Equal(titles[..SearchPageViewModel.LocalShownCollapsed], Shelf(vm));
            Assert.True(vm.HasLocalResults);
            Assert.True(vm.CanShowMoreLocal);
            Assert.Equal("120 songs", vm.LocalCount);
            Assert.Null(vm.LocalCapped);

            vm.ShowMoreLocalCommand.Execute(null);

            Assert.Equal(titles, Shelf(vm));
            Assert.False(vm.CanShowMoreLocal);
            Assert.Equal("Showing the first 8 of 120. Add words to narrow the search.", vm.LocalCapped);
        });
    }

    [Fact]
    public void WhenYouTubeMusicFails_TheLocalMatchesStay()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            Search(vm, "blue");
            _local[0].Complete(1, "Blue");
            _catalog.Searches[0].Fail("offline");

            Assert.Equal(["Blue"], Shelf(vm));
            Assert.NotNull(vm.LoadError);
            Assert.False(vm.IsBusy);
        });
    }

    [Fact]
    public void ASupersededSearch_DoesntOverwriteTheNewerShelf()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            Search(vm, "blue");
            Search(vm, "red");

            _local[1].Complete(1, "Red");
            _local[0].Complete(1, "Blue"); // cancelled already; ignored if it lands
            _catalog.Searches[1].Complete("Red (YouTube)");

            Assert.Equal(["Red"], Shelf(vm));
        });
    }

    [Fact]
    public void AnEmptyBox_AndAFilteredTab_EmptyTheShelf()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            Search(vm, "blue");
            _local[0].Complete(1, "Blue");
            _catalog.Searches[0].Complete("Blue (YouTube)");

            vm.SelectedTab = new(SearchFilter.Songs);
            Assert.Empty(vm.LocalResults);
            Assert.False(vm.HasLocalResults);
            Assert.Single(_local); // a filtered tab doesn't search the files

            vm.SelectedTab = SearchTab.All;
            _local[1].Complete(1, "Blue");
            Assert.True(vm.HasLocalResults);

            vm.Query = "";
            vm.SearchCommand.Execute(null);
            Assert.False(vm.HasLocalResults);
        });
    }

    [Fact]
    public void ALocalSearchThatFails_EmptiesTheShelf_AndYouTubeMusicStillShows()
    {
        Inline(() =>
        {
            var vm = CreateVm();
            Search(vm, "blue");
            _local[0].Result.SetException(new IOException("index unreadable"));
            _catalog.Searches[0].Complete("Blue (YouTube)");

            Assert.False(vm.HasLocalResults);
            Assert.Equal(["Blue (YouTube)"], vm.Results.Select(r => r.Title));
            Assert.Null(vm.LoadError);
        });
    }

    [Fact]
    public async Task ALocalRow_PlaysTheShelf_StartingAtIt_WithoutYouTubeResults()
    {
        var vm = CreateVm();
        Inline(() =>
        {
            Search(vm, "blue");
            _local[0].Complete(3, "Blue 1", "Blue 2", "Blue 3");
            _catalog.Searches[0].Complete("Blue (YouTube)");
        });

        await vm.PlayLocalCommand.ExecuteAsync(vm.LocalResults[1]);

        Assert.Equal(vm.LocalResults.Select(r => r.Id), _queue.Items.Select(t => t.VideoId));
        Assert.Equal(1, _queue.CurrentIndex);
    }
}
