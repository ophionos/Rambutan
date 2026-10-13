using System.Net.Http;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;
using Rambutan.Core.Remote;
using Rambutan.Core.Settings;

namespace Rambutan.Core.Tests;

/// <summary>The queue and search a phone reaches through <see cref="RemoteSession"/>.</summary>
public class RemoteLibraryTests
{
    private readonly FakeStreamSource _streams = new();
    private readonly FakeAudioPlayer _player = new();
    private readonly QueueModel _queue = new();
    private SongSearch _search = (_, _, _) => Task.FromResult(new RemoteSongPage([]));

    private PlaybackCoordinator CreateCoordinator() => new(_streams, _player, _queue, new PlaybackSettings());

    private RemoteSession CreateSession(PlaybackCoordinator coordinator) =>
        new(coordinator, action => action(), track => track.ThumbnailUrl is not null,
            searchSongs: (q, next, ct) => _search(q, next, ct));

    private static TrackInfo[] Tracks(params string[] ids) => ids.Select(id => TestTracks.Track(id)).ToArray();

    private void SearchFinds(params string[] ids)
    {
        var tracks = Tracks(ids);
        _search = (_, _, _) => Task.FromResult(new RemoteSongPage(tracks));
    }

    [Fact]
    public void AQueueChange_IsPublishedAsANewVersion()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        var changes = 0;
        session.Changed += () => changes++;
        var before = session.Current.QueueVersion;

        _queue.Enqueue(TestTracks.Track("t1"));

        Assert.Equal(1, changes);
        Assert.True(session.Current.QueueVersion > before);
        Assert.Equal(session.Current.QueueVersion, session.GetQueue().Version);
    }

    [Fact]
    public void TheQueue_IsTheTracksWithTheirPositionsAndTheCurrentOne()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        _queue.SetQueue(Tracks("a", "b", "c"), 1);

        var queue = session.GetQueue();

        Assert.Equal(1, queue.CurrentIndex);
        Assert.Equal(3, queue.Total);
        Assert.Equal(["a", "b", "c"], queue.Items.Select(i => i.VideoId));
        Assert.Equal([0, 1, 2], queue.Items.Select(i => i.Index));
        Assert.Equal(new RemoteQueueItem(0, "a", "Title a", "Artist", 100_000, false), queue.Items[0]);
    }

    [Fact]
    public void AnEmptyQueue_HasNoCurrentTrack()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);

        var queue = session.GetQueue();

        Assert.Equal(-1, queue.CurrentIndex);
        Assert.Equal(0, queue.Total);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public void ALongQueue_IsAWindowAroundTheCurrentTrack()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        _queue.SetQueue(Enumerable.Range(0, 1000).Select(i => TestTracks.Track($"t{i}")), 500);

        var queue = session.GetQueue();

        Assert.Equal(1000, queue.Total);
        Assert.Equal(500 - RemoteQueue.Before, queue.Items[0].Index);
        Assert.Equal(500 + RemoteQueue.From - 1, queue.Items[^1].Index);
        Assert.Equal("t480", queue.Items[0].VideoId);
    }

    [Fact]
    public void AutoplayTracks_AreMarked()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        var seed = TestTracks.Track("seed");
        _queue.SetQueue([seed]);
        _queue.AppendAutoplay(seed, Tracks("r1"));

        var items = session.GetQueue().Items;

        Assert.False(items[0].Autoplay);
        Assert.True(items[1].Autoplay);
    }

    [Fact]
    public async Task Jump_PlaysTheTrackAtThatPosition()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        await coordinator.PlayQueueAsync(Tracks("a", "b", "c"));

        session.Execute(new RemoteQueueCommand.Jump(2, "c"));

        await TestTracks.WaitUntilAsync(() => coordinator.NowPlaying?.VideoId == "c");
    }

    [Fact]
    public async Task Commands_ForAPositionThatNowHoldsAnotherTrack_DoNothing()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        await coordinator.PlayQueueAsync(Tracks("a", "b", "c"));
        // The phone saw "c" at 2; the desktop has since removed "b".
        _queue.Remove(1);

        session.Execute(new RemoteQueueCommand.Jump(2, "c"));
        session.Execute(new RemoteQueueCommand.Remove(1, "b"));
        session.Execute(new RemoteQueueCommand.Move(1, "b", 0));

        Assert.Equal(["a", "c"], _queue.Items.Select(t => t.VideoId));
        Assert.Equal("a", coordinator.NowPlaying!.VideoId);
    }

    [Fact]
    public async Task Remove_TakesOutAnUpcomingTrackButNotTheCurrentOne()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        await coordinator.PlayQueueAsync(Tracks("a", "b", "c"));

        session.Execute(new RemoteQueueCommand.Remove(0, "a"));
        session.Execute(new RemoteQueueCommand.Remove(2, "c"));

        Assert.Equal(["a", "b"], _queue.Items.Select(t => t.VideoId));
    }

    [Fact]
    public async Task Move_Reorders()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        await coordinator.PlayQueueAsync(Tracks("a", "b", "c"));

        session.Execute(new RemoteQueueCommand.Move(2, "c", 1));
        session.Execute(new RemoteQueueCommand.Move(1, "c", 99)); // out of range: ignored

        Assert.Equal(["a", "c", "b"], _queue.Items.Select(t => t.VideoId));
    }

    [Fact]
    public async Task Search_ReturnsTheSongs()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        _search = (q, _, _) => Task.FromResult(new RemoteSongPage(
            [TestTracks.Track("s1") with { Title = q, Album = "Album" }, TestTracks.Track("s1")]));

        var outcome = await session.SearchAsync("query", CancellationToken.None);

        Assert.Null(outcome.Failure);
        Assert.Equal([new RemoteSearchResult("s1", "query", "Artist", "Album", 100_000)], outcome.Results);
    }

    [Fact]
    public async Task Search_SaysWhenTheComputerIsOffline()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        _search = (_, _, _) => throw new HttpRequestException("no route");

        var outcome = await session.SearchAsync("q", CancellationToken.None);

        Assert.Equal(RemoteSearchFailure.Offline, outcome.Failure);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task Search_OtherFailures_AreReportedNotThrown()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        _search = (_, _, _) => throw new InvalidOperationException("parser broke");

        var outcome = await session.SearchAsync("q", CancellationToken.None);

        Assert.Equal(RemoteSearchFailure.Failed, outcome.Failure);
    }

    [Fact]
    public async Task Search_WithoutACatalog_Fails()
    {
        using var coordinator = CreateCoordinator();
        using var session = new RemoteSession(coordinator, action => action());

        var outcome = await session.SearchAsync("q", CancellationToken.None);

        Assert.Equal(RemoteSearchFailure.Failed, outcome.Failure);
    }

    [Fact]
    public async Task Search_ACancelledRequest_Throws()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        _search = (_, _, ct) => Task.FromCanceled<RemoteSongPage>(ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SearchAsync("q", cts.Token));
    }

    [Fact]
    public async Task Add_OnlyAcceptsWhatASearchFound()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        await coordinator.PlayQueueAsync(Tracks("a"));
        SearchFinds("s1");
        await session.SearchAsync("q", CancellationToken.None);

        Assert.False(session.Add("never-found", next: false));
        Assert.True(session.Add("s1", next: false));
        Assert.Equal(["a", "s1"], _queue.Items.Select(t => t.VideoId));
    }

    [Fact]
    public async Task Add_Next_GoesRightAfterTheCurrentTrack()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        await coordinator.PlayQueueAsync(Tracks("a", "b"));
        SearchFinds("s1", "s2");
        await session.SearchAsync("q", CancellationToken.None);

        session.Add("s1", next: false);
        session.Add("s2", next: true);

        Assert.Equal(["a", "s2", "b", "s1"], _queue.Items.Select(t => t.VideoId));
        Assert.Equal("a", coordinator.NowPlaying!.VideoId);
    }

    [Fact]
    public async Task Add_TheSameSongTwice_MakesTwoRows()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        await coordinator.PlayQueueAsync(Tracks("a"));
        SearchFinds("s1");
        await session.SearchAsync("q", CancellationToken.None);

        session.Add("s1", next: false);
        session.Add("s1", next: false);

        var items = _queue.Items;
        Assert.Equal(3, items.Count);
        Assert.NotSame(items[1], items[2]);
    }

    [Fact]
    public async Task Add_WithNothingPlaying_StartsTheAddedTrack()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        // A queue that played out: stopped, with its last track current.
        _queue.SetQueue(Tracks("old1", "old2"), 1);
        SearchFinds("s1");
        await session.SearchAsync("q", CancellationToken.None);

        session.Add("s1", next: false);

        await TestTracks.WaitUntilAsync(() => coordinator.NowPlaying?.VideoId == "s1");
    }

    [Fact]
    public async Task OnlyTheMostRecentlyFoundSongs_AreKept()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        SearchFinds("first");
        await session.SearchAsync("q1", CancellationToken.None);
        SearchFinds(Enumerable.Range(0, RemoteSession.FoundLimit).Select(i => $"n{i}").ToArray());
        await session.SearchAsync("q2", CancellationToken.None);

        Assert.False(session.Add("first", next: false));
        Assert.True(session.Add("n0", next: false));
    }

    [Fact]
    public async Task FindingASongAgain_KeepsItAsLongAsANewOne()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        SearchFinds("early");
        await session.SearchAsync("q1", CancellationToken.None);
        SearchFinds(Enumerable.Range(0, RemoteSession.FoundLimit - 1).Select(i => $"n{i}").ToArray());
        await session.SearchAsync("q2", CancellationToken.None);
        SearchFinds("early");
        await session.SearchAsync("q3", CancellationToken.None);

        SearchFinds("last");
        await session.SearchAsync("q4", CancellationToken.None);

        // "n0" was the least recently found, not "early".
        Assert.True(session.Add("early", next: false));
        Assert.False(session.Add("n0", next: false));
    }

    [Fact]
    public async Task More_FetchesTheNextPage_WithTheCatalogsToken()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        var asked = new List<(string Query, string? Next)>();
        _search = (q, next, _) =>
        {
            asked.Add((q, next?.Value));
            return Task.FromResult(next is null
                ? new RemoteSongPage(Tracks("p1"), new ContinuationToken("page2"))
                : new RemoteSongPage(Tracks("p2")));
        };

        var first = await session.SearchAsync("query", CancellationToken.None);
        Assert.NotNull(first.More);
        Assert.DoesNotContain("page2", first.More); // the host's key, not the catalog's token

        var second = await session.MoreAsync(first.More!, CancellationToken.None);

        Assert.Equal(["p2"], second!.Results.Select(r => r.VideoId));
        Assert.Null(second.More);
        Assert.Equal([("query", (string?)null), ("query", "page2")], asked);
        Assert.True(session.Add("p2", next: false)); // a later page's songs can be added too
    }

    [Fact]
    public async Task More_ForAKeyTheSessionNeverGave_IsNull()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);

        Assert.Null(await session.MoreAsync("made-up", CancellationToken.None));
    }

    [Fact]
    public async Task More_OnlyTheLastFewKeysAreKept()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        _search = (_, _, _) => Task.FromResult(new RemoteSongPage(Tracks("s"), new ContinuationToken("next")));
        var keys = new List<string>();
        for (var i = 0; i <= RemoteSession.PageLimit; i++)
            keys.Add((await session.SearchAsync($"q{i}", CancellationToken.None)).More!);

        Assert.Null(await session.MoreAsync(keys[0], CancellationToken.None));
        Assert.NotNull(await session.MoreAsync(keys[^1], CancellationToken.None));
    }

    [Fact]
    public async Task Play_ReplacesTheQueueWithTheSong_AndPlaysIt()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        await coordinator.PlayQueueAsync(Tracks("a", "b"));
        SearchFinds("s1");
        await session.SearchAsync("q", CancellationToken.None);

        Assert.False(session.Play("never-found"));
        Assert.True(session.Play("s1"));

        await TestTracks.WaitUntilAsync(() => coordinator.NowPlaying?.VideoId == "s1");
        Assert.Equal(["s1"], _queue.Items.Select(t => t.VideoId));
    }

    [Fact]
    public async Task Covers_AreServedForTheQueueAndFoundSongs_Only()
    {
        using var coordinator = CreateCoordinator();
        using var session = CreateSession(coordinator);
        static TrackInfo WithCover(string id) => TestTracks.Track(id) with { ThumbnailUrl = $"https://example.test/{id}.jpg" };
        await coordinator.PlayQueueAsync([WithCover("playing"), WithCover("queued"), TestTracks.Track("bare")]);
        _search = (_, _, _) => Task.FromResult(new RemoteSongPage([WithCover("found")]));
        var outcome = await session.SearchAsync("q", CancellationToken.None);

        Assert.True(session.HasCover("playing"));
        Assert.True(session.HasCover("queued"));
        Assert.True(session.HasCover("found"));
        Assert.False(session.HasCover("bare")); // queued, but has no cover
        Assert.False(session.HasCover("elsewhere"));
        Assert.Equal([true, true, false], session.GetQueue().Items.Select(i => i.HasArtwork));
        Assert.True(outcome.Results[0].HasArtwork);
    }
}
