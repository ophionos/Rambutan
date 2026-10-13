using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

public class QueueViewModelTests
{
    private static TrackInfo Track(string id) =>
        new() { VideoId = id, Title = id.ToUpperInvariant(), Artists = [new("Artist")] };

    private static (QueueModel Queue, QueueViewModel Vm) Create(params string[] ids)
    {
        var queue = new QueueModel();
        queue.SetQueue(ids.Select(Track));
        return (queue, new QueueViewModel(queue, null));
    }

    private static string[] Ids(QueueViewModel vm) => vm.Items.Select(i => i.Track.VideoId).ToArray();

    [Fact]
    public void AdvancingTheSong_KeepsEveryRowInstance() => Headless.Run(() =>
    {
        var (queue, vm) = Create("a", "b", "c");
        var before = vm.Items.ToArray();

        queue.NextTrack(TrackEndReason.Natural);
        vm.Refresh();

        Assert.Equal(before, vm.Items);
        Assert.False(vm.Items[0].IsCurrent);
        Assert.True(vm.Items[1].IsCurrent);
        Assert.True(vm.Items[0].IsHistory);
    });

    [Fact]
    public void Reorder_MovesTheRow_InOneCollectionChange() => Headless.Run(() =>
    {
        var (queue, vm) = Create("a", "b", "c", "d", "e");
        var moved = vm.Items[4];
        var changes = new List<NotifyCollectionChangedAction>();
        vm.Items.CollectionChanged += (_, e) => changes.Add(e.Action);

        vm.ReorderCommand.Execute(new ReorderRequest(4, 1));
        vm.Refresh();

        Assert.Equal(["a", "e", "b", "c", "d"], Ids(vm));
        Assert.Same(moved, vm.Items[1]);
        Assert.Equal([NotifyCollectionChangedAction.Move], changes);
        Assert.Equal(1, moved.Index);
    });

    [Fact]
    public void MovingARowDown_IsAlsoOneChange() => Headless.Run(() =>
    {
        var (_, vm) = Create("a", "b", "c", "d", "e");
        var changes = new List<NotifyCollectionChangedAction>();
        vm.Items.CollectionChanged += (_, e) => changes.Add(e.Action);

        vm.ReorderCommand.Execute(new ReorderRequest(1, 4));
        vm.Refresh();

        Assert.Equal(["a", "c", "d", "e", "b"], Ids(vm));
        Assert.Equal([NotifyCollectionChangedAction.Move], changes);
    });

    [Fact]
    public void RemoveAndEnqueue_TouchOnlyTheirRows() => Headless.Run(() =>
    {
        var (queue, vm) = Create("a", "b", "c");
        var a = vm.Items[0];
        var c = vm.Items[2];

        queue.Remove(1);
        queue.Enqueue(Track("d"));
        vm.Refresh();

        Assert.Equal(["a", "c", "d"], Ids(vm));
        Assert.Same(a, vm.Items[0]);
        Assert.Same(c, vm.Items[1]);
    });

    [Fact]
    public void TheSameSongQueuedTwice_IsTwoRows() => Headless.Run(() =>
    {
        var queue = new QueueModel();
        var song = Track("a");
        queue.SetQueue([song, Track("b"), song]);
        var vm = new QueueViewModel(queue, null);
        var first = vm.Items[0];
        var last = vm.Items[2];

        queue.Move(2, 1);
        vm.Refresh();

        Assert.Equal(3, vm.Items.Count);
        Assert.Equal(["a", "a", "b"], Ids(vm));
        Assert.Equal(2, new[] { first, last }.Intersect(vm.Items).Count());
    });

    [Fact]
    public void Sync_HandlesAWholesaleReplacement()
    {
        var items = new ObservableCollection<QueueItemViewModel>(
            new[] { "a", "b", "c" }.Select(id => new QueueItemViewModel(Track(id))));
        var tracks = new[] { Track("x"), items[2].Track, Track("y"), items[0].Track };

        QueueViewModel.Sync(items, tracks);

        Assert.Equal(tracks, items.Select(i => i.Track));
    }

    [Fact]
    public void Sections_LabelPlayedCurrentAndUpNext() => Headless.Run(() =>
    {
        var (queue, vm) = Create("a", "b", "c", "d");
        queue.JumpTo(1);
        vm.Refresh();

        // No coordinator means nothing is loaded: the restored-session label.
        Assert.Equal(["Played", "Where you left off", "Up next (2)", null],
            vm.Items.Select(i => i.Section));
    });

    [Fact]
    public void CurrentChanged_FiresOnANewTrack_NotOnAReorder() => Headless.Run(() =>
    {
        var (queue, vm) = Create("a", "b", "c");
        var seen = new List<string>();
        vm.CurrentChanged += item => seen.Add(item.Track.VideoId);

        queue.Move(2, 1);
        vm.Refresh();
        Assert.Empty(seen);

        queue.NextTrack(TrackEndReason.Manual);
        vm.Refresh();
        Assert.Equal(["c"], seen);
    });

    [Theory]
    [InlineData(0, 2)] // before the current track: lands right after it
    [InlineData(4, 3)] // after it: same
    public void PlayNext_OnAQueuedRow_MovesItAfterTheCurrentTrack(int from, int expected) => Headless.Run(() =>
    {
        var (queue, vm) = Create("a", "b", "c", "d", "e");
        queue.JumpTo(2);
        vm.Refresh();
        var row = vm.Items[from];

        vm.PlayNextCommand.Execute(row);
        vm.Refresh();

        Assert.Same(row, vm.Items[expected]);
        Assert.Equal(expected - 1, queue.CurrentIndex);
    });

    [Fact]
    public void AutoplayTracks_GetTheirOwnCaption_AndUpNextCountsOnlyTheUsers() => Headless.Run(() =>
    {
        var (queue, vm) = Create("a", "b");
        queue.AppendAutoplay(queue.Items[1], [Track("x"), Track("y")]);
        vm.Refresh();

        Assert.Equal(["Now playing", "Up next (1)", "Autoplay", null],
            vm.Items.Select(i => i.Section == "Where you left off" ? "Now playing" : i.Section));
    });

    [Fact]
    public void WhenOnlyAutoplayFollows_ItIsCaptionedAutoplay() => Headless.Run(() =>
    {
        var (queue, vm) = Create("a");
        queue.AppendAutoplay(queue.Items[0], [Track("x")]);
        queue.NextTrack(TrackEndReason.Natural);
        queue.AppendAutoplay(queue.Items[1], [Track("y"), Track("z")]);
        vm.Refresh();

        // The autoplay track now playing is captioned as playing; the rest as autoplay.
        Assert.Equal(["Played", null, "Autoplay", null], vm.Items.Select(i =>
            i.Section is "Now playing" or "Where you left off" ? null : i.Section));
    });
}
