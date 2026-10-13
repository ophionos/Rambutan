using Rambutan.Core.Queue;

namespace Rambutan.Core.Tests;

public class QueueModelTests
{
    private static QueueModel Queue(params string[] ids)
    {
        var queue = new QueueModel();
        queue.SetQueue(ids.Select(id => TestTracks.Track(id)));
        return queue;
    }

    [Fact]
    public void SetQueue_ClampsStartIndex()
    {
        var queue = new QueueModel();
        queue.SetQueue([TestTracks.Track("a"), TestTracks.Track("b")], startIndex: 99);
        Assert.Equal(1, queue.CurrentIndex);
    }

    [Fact]
    public void NextTrack_Advances_AndStopsAtEnd()
    {
        var queue = Queue("a", "b");
        Assert.Equal("b", queue.NextTrack(TrackEndReason.Natural)!.VideoId);
        Assert.Null(queue.NextTrack(TrackEndReason.Natural));
        Assert.Equal(1, queue.CurrentIndex); // index untouched when null
    }

    [Fact]
    public void RepeatAll_WrapsAtEnd()
    {
        var queue = Queue("a", "b");
        queue.Repeat = RepeatMode.All;
        queue.NextTrack(TrackEndReason.Natural);
        Assert.Equal("a", queue.NextTrack(TrackEndReason.Natural)!.VideoId);
    }

    [Fact]
    public void RepeatOne_RepeatsOnNaturalEnd_AdvancesOnManual()
    {
        var queue = Queue("a", "b");
        queue.Repeat = RepeatMode.One;
        Assert.Equal("a", queue.NextTrack(TrackEndReason.Natural)!.VideoId);
        Assert.Equal("b", queue.NextTrack(TrackEndReason.Manual)!.VideoId);
    }

    [Fact]
    public void Repeat_RaisesChanged_OnlyWhenItChanges()
    {
        var queue = Queue("a", "b");
        var changes = 0;
        queue.Changed += () => changes++;

        queue.Repeat = RepeatMode.All;
        queue.Repeat = RepeatMode.All;
        queue.Repeat = RepeatMode.Off;

        Assert.Equal(2, changes);
    }

    [Fact]
    public void PeekNext_DoesNotMutate()
    {
        var queue = Queue("a", "b");
        Assert.Equal("b", queue.PeekNext(TrackEndReason.Natural)!.VideoId);
        Assert.Equal(0, queue.CurrentIndex);
    }

    [Fact]
    public void PreviousTrack_NullAtStart()
    {
        var queue = Queue("a", "b");
        Assert.Null(queue.PreviousTrack());
        queue.NextTrack(TrackEndReason.Manual);
        Assert.Equal("a", queue.PreviousTrack()!.VideoId);
    }

    [Fact]
    public void EnqueueNext_InsertsAfterCurrent()
    {
        var queue = Queue("a", "b");
        queue.EnqueueNext(TestTracks.Track("x"));
        Assert.Equal(["a", "x", "b"], queue.Items.Select(t => t.VideoId));
    }

    [Fact]
    public void Remove_BeforeCurrent_AdjustsIndex()
    {
        var queue = Queue("a", "b", "c");
        queue.JumpTo(2);
        Assert.True(queue.Remove(0));
        Assert.Equal(1, queue.CurrentIndex);
        Assert.Equal("c", queue.Current!.VideoId);
    }

    [Fact]
    public void Remove_Current_IsRefused()
    {
        var queue = Queue("a", "b");
        Assert.False(queue.Remove(0));
    }

    [Fact]
    public void Move_TrackAcrossCurrent_KeepsCurrentStable()
    {
        var queue = Queue("a", "b", "c");
        queue.JumpTo(1);
        queue.Move(2, 0); // c moves before current b
        Assert.Equal("b", queue.Current!.VideoId);
        Assert.Equal(["c", "a", "b"], queue.Items.Select(t => t.VideoId));
    }

    private static string[] Ids(QueueModel queue) => queue.Items.Select(t => t.VideoId).ToArray();

    private static QueueModel Shuffled(int seed, params string[] ids)
    {
        var queue = new QueueModel(new Random(seed));
        queue.SetQueue(ids.Select(id => TestTracks.Track(id)));
        return queue;
    }

    [Fact]
    public void ShuffleOn_KeepsPlayedAndCurrent_ShufflesOnlyUpcoming()
    {
        var queue = Shuffled(42, "a", "b", "c", "d", "e", "f", "g");
        queue.JumpTo(1);

        queue.SetShuffle(true);

        var ids = Ids(queue);
        Assert.True(queue.Shuffle);
        Assert.Equal(["a", "b"], ids[..2]);
        Assert.Equal(1, queue.CurrentIndex);
        Assert.Equal(["c", "d", "e", "f", "g"], ids[2..].Order());
        Assert.NotEqual(["c", "d", "e", "f", "g"], ids[2..]); // seed 42 does move them
    }

    [Fact]
    public void ShuffleOff_RestoresOriginalOrder_CurrentAtItsOriginalPosition()
    {
        var queue = Shuffled(7, "a", "b", "c", "d", "e", "f");
        queue.SetShuffle(true);
        queue.NextTrack(TrackEndReason.Manual);
        queue.NextTrack(TrackEndReason.Manual);
        var playing = queue.Current!.VideoId;

        queue.SetShuffle(false);

        Assert.False(queue.Shuffle);
        Assert.Equal(["a", "b", "c", "d", "e", "f"], Ids(queue));
        Assert.Equal(playing, queue.Current!.VideoId);
        Assert.Equal(Array.IndexOf(Ids(queue), playing), queue.CurrentIndex);
    }

    [Fact]
    public void SetShuffle_ToTheSameMode_ChangesNothing()
    {
        var queue = Shuffled(1, "a", "b", "c", "d");
        var changes = 0;
        queue.Changed += () => changes++;

        queue.SetShuffle(false);
        Assert.Equal(0, changes);

        queue.SetShuffle(true);
        var once = Ids(queue);
        queue.SetShuffle(true);
        Assert.Equal(once, Ids(queue));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SetQueue_WhileShuffled_PlaysChosenTrackFirst_ThenShufflesTheRest()
    {
        var queue = new QueueModel(new Random(3));
        queue.SetShuffle(true);

        queue.SetQueue(new[] { "a", "b", "c", "d", "e", "f" }.Select(id => TestTracks.Track(id)), startIndex: 3);

        Assert.Equal(0, queue.CurrentIndex);
        Assert.Equal("d", queue.Current!.VideoId);
        Assert.Equal(["a", "b", "c", "d", "e", "f"], Ids(queue).Order());

        queue.SetShuffle(false);
        Assert.Equal(["a", "b", "c", "d", "e", "f"], Ids(queue));
        Assert.Equal(3, queue.CurrentIndex);
    }

    [Fact]
    public void EditsWhileShuffled_CarryIntoTheOriginalOrder()
    {
        var queue = Shuffled(5, "a", "b", "c", "d", "e");
        queue.JumpTo(1); // b
        queue.SetShuffle(true);

        queue.Enqueue(TestTracks.Track("end"));
        queue.EnqueueNext(TestTracks.Track("next"));
        Assert.Equal("next", queue.Items[2].VideoId); // plays right after b
        Assert.Equal("end", queue.Items[^1].VideoId);
        queue.Remove(Array.IndexOf(Ids(queue), "d"));
        queue.Move(Array.IndexOf(Ids(queue), "e"), 2); // reorders the shuffled order only

        queue.SetShuffle(false);

        Assert.Equal(["a", "b", "next", "c", "e", "end"], Ids(queue));
        Assert.Equal("b", queue.Current!.VideoId);
    }

    [Fact]
    public void ExtendFromSeed_WhileShuffled_ShufflesArrivals_OriginalOrderKept()
    {
        var seed = TestTracks.Track("seed");
        var queue = new QueueModel(new Random(11));
        queue.SetShuffle(true);
        queue.SetQueue([seed]);

        var radio = new[] { "r1", "r2", "r3", "r4", "r5", "r6" };
        queue.ExtendFromSeed(seed, radio.Select(id => TestTracks.Track(id)));

        Assert.Equal("seed", queue.Items[0].VideoId);
        Assert.Equal(radio, Ids(queue)[1..].Order());
        Assert.NotEqual(radio, Ids(queue)[1..]);

        queue.SetShuffle(false);
        Assert.Equal(["seed", .. radio], Ids(queue));
    }

    [Fact]
    public void Shuffle_OnAnEmptyQueue_IsHarmless()
    {
        var queue = new QueueModel(new Random(0));
        queue.SetShuffle(true);
        queue.EnqueueNext(TestTracks.Track("a"));
        queue.Enqueue(TestTracks.Track("b"));
        queue.SetShuffle(false);

        Assert.Equal(["a", "b"], Ids(queue));
        Assert.Equal(0, queue.CurrentIndex);
    }

    [Fact]
    public void ExtendFromSeed_AppendsAfterSeed_SkippingTheSeedItself()
    {
        var seed = TestTracks.Track("seed");
        var queue = new QueueModel();
        queue.SetQueue([seed]);

        // A radio leads with its own seed.
        var added = queue.ExtendFromSeed(
            seed, [TestTracks.Track("seed"), TestTracks.Track("r1"), TestTracks.Track("r2")]);

        Assert.Equal(2, added);
        Assert.Equal(["seed", "r1", "r2"], queue.Items.Select(t => t.VideoId));
        Assert.Equal(0, queue.CurrentIndex);
    }

    [Fact]
    public void ExtendFromSeed_KeepsWhatTheUserQueuedMeanwhile_AndSkipsDuplicates()
    {
        var seed = TestTracks.Track("seed");
        var queue = new QueueModel();
        queue.SetQueue([seed]);
        queue.Enqueue(TestTracks.Track("mine"));

        queue.ExtendFromSeed(seed, [TestTracks.Track("mine"), TestTracks.Track("r1")]);

        Assert.Equal(["seed", "mine", "r1"], queue.Items.Select(t => t.VideoId));
    }

    [Fact]
    public void ExtendFromSeed_DropsLateArrivals_WhenTheQueueWasReplaced()
    {
        var seed = TestTracks.Track("seed");
        var queue = new QueueModel();
        queue.SetQueue([seed]);
        var changes = 0;
        queue.SetQueue([TestTracks.Track("album1"), TestTracks.Track("album2")]);
        queue.Changed += () => changes++;

        var added = queue.ExtendFromSeed(seed, [TestTracks.Track("r1")]);

        Assert.Equal(0, added);
        Assert.Equal(0, changes);
        Assert.Equal(["album1", "album2"], queue.Items.Select(t => t.VideoId));
    }

    [Fact]
    public void ExtendFromSeed_RequiresTheSameInstance_NotJustTheSameId()
    {
        // Replaying the same song replaces the queue; the first play's radio must not
        // land on the second play's queue as if nothing had happened.
        var first = TestTracks.Track("seed");
        var queue = new QueueModel();
        queue.SetQueue([TestTracks.Track("seed")]);

        Assert.Equal(0, queue.ExtendFromSeed(first, [TestTracks.Track("r1")]));
    }

    [Fact]
    public void Move_Downward_PastCurrent_ShiftsCurrentBack()
    {
        var queue = Queue("a", "b", "c", "d");
        queue.JumpTo(2);
        queue.Move(0, 3); // a moves after current c
        Assert.Equal(["b", "c", "d", "a"], queue.Items.Select(t => t.VideoId));
        Assert.Equal("c", queue.Current!.VideoId);
        Assert.Equal(1, queue.CurrentIndex);
    }

    [Fact]
    public void Move_TheCurrentTrack_FollowsIt()
    {
        var queue = Queue("a", "b", "c");
        queue.Move(0, 2);
        Assert.Equal(["b", "c", "a"], queue.Items.Select(t => t.VideoId));
        Assert.Equal(2, queue.CurrentIndex);
    }

    [Fact]
    public void Move_ToTheSamePlaceOrOutOfRange_DoesNotRaiseChanged()
    {
        var queue = Queue("a", "b");
        var changes = 0;
        queue.Changed += () => changes++;
        queue.Move(1, 1);
        queue.Move(1, 5);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Snapshot_CapturesTracksIndexAndRepeat()
    {
        var queue = Queue("a", "b", "c");
        queue.JumpTo(1);
        queue.Repeat = RepeatMode.All;

        var snapshot = queue.Snapshot();

        Assert.Equal(["a", "b", "c"], snapshot.Tracks.Select(t => t.VideoId));
        Assert.Equal(1, snapshot.CurrentIndex);
        Assert.Equal(RepeatMode.All, snapshot.Repeat);

        // A copy: later edits don't reach it.
        queue.Enqueue(TestTracks.Track("d"));
        Assert.Equal(3, snapshot.Tracks.Count);
    }

    [Fact]
    public void Restore_PutsTheQueueBack_WithOneChange()
    {
        var source = Queue("a", "b", "c");
        source.JumpTo(2);
        source.Repeat = RepeatMode.One;
        var snapshot = source.Snapshot();

        var queue = new QueueModel();
        var changes = 0;
        queue.Changed += () => changes++;
        queue.Restore(snapshot);

        Assert.Equal(["a", "b", "c"], queue.Items.Select(t => t.VideoId));
        Assert.Equal(2, queue.CurrentIndex);
        Assert.Equal(RepeatMode.One, queue.Repeat);
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(99, 1)]
    public void Restore_ClampsAnIndexOutOfRange(int saved, int expected)
    {
        var queue = new QueueModel();
        queue.Restore(new QueueSnapshot(
            [TestTracks.Track("a"), TestTracks.Track("b")], saved, RepeatMode.Off));
        Assert.Equal(expected, queue.CurrentIndex);
    }

    [Fact]
    public void Restore_EmptySnapshot_LeavesNoCurrentTrack()
    {
        var queue = Queue("a");
        queue.Restore(new QueueSnapshot([], 0, RepeatMode.Off));
        Assert.Empty(queue.Items);
        Assert.Equal(-1, queue.CurrentIndex);
    }

    [Fact]
    public void Snapshot_WhileShuffled_CarriesTheOriginalOrder()
    {
        var queue = new QueueModel(new Random(7));
        queue.SetQueue(new[] { "a", "b", "c", "d", "e", "f" }.Select(id => TestTracks.Track(id)));
        queue.SetShuffle(true);

        var snapshot = queue.Snapshot();

        Assert.True(snapshot.Shuffle);
        Assert.NotNull(snapshot.ShuffleRanks);
        // Rank i of each track is where it sat before shuffling: a, b, c … = 0, 1, 2 …
        for (var i = 0; i < snapshot.Tracks.Count; i++)
            Assert.Equal(snapshot.Tracks[i].VideoId[0] - 'a', snapshot.ShuffleRanks![i]);
    }

    [Fact]
    public void Snapshot_Unshuffled_HasNoRanks() =>
        Assert.False(Queue("a", "b").Snapshot().Shuffle);

    [Fact]
    public void Restore_Shuffled_KeepsTheSavedOrder_AndShuffleOffStillRestoresTheOriginal()
    {
        var tracks = new[] { "c", "a", "b" }.Select(id => TestTracks.Track(id)).ToArray();
        var queue = new QueueModel(new Random(1));

        queue.Restore(new QueueSnapshot(tracks, 1, RepeatMode.Off, [2, 0, 1]));

        Assert.True(queue.Shuffle);
        Assert.Equal(["c", "a", "b"], queue.Items.Select(t => t.VideoId)); // not reshuffled
        Assert.Equal("a", queue.Current!.VideoId);

        queue.SetShuffle(false);
        Assert.Equal(["a", "b", "c"], queue.Items.Select(t => t.VideoId));
        Assert.Equal("a", queue.Current!.VideoId);
    }

    [Fact]
    public void Restore_Unshuffled_TurnsShuffleOff()
    {
        var queue = Queue("x", "y");
        queue.SetShuffle(true);

        queue.Restore(new QueueSnapshot([TestTracks.Track("a"), TestTracks.Track("b")], 0, RepeatMode.Off));

        Assert.False(queue.Shuffle);
        Assert.Equal(["a", "b"], queue.Items.Select(t => t.VideoId));
    }

    [Theory]
    [InlineData(new[] { 0, 0, 1 })]
    [InlineData(new[] { 0, 1 })]
    [InlineData(new[] { 0, 1, 3 })]
    public void Restore_RanksThatArentAPermutation_RestoreUnshuffled(int[] ranks)
    {
        var queue = new QueueModel();
        queue.Restore(new QueueSnapshot(
            new[] { "a", "b", "c" }.Select(id => TestTracks.Track(id)).ToArray(), 0, RepeatMode.Off, ranks));

        Assert.False(queue.Shuffle);
        Assert.Equal(["a", "b", "c"], queue.Items.Select(t => t.VideoId));
    }
}
