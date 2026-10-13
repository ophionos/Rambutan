using Rambutan.Integrations.LastFm;

namespace Rambutan.Integrations.Tests.LastFm;

public sealed class ScrobbleQueueTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Enqueued_SurvivesARestart_InOrder()
    {
        var queue = new ScrobbleQueue(_dir.Path);
        queue.Enqueue(Tracks.Entry("One", 100) with { Album = "Discovery", DurationSeconds = 320 });
        queue.Enqueue(Tracks.Entry("Two", 200));

        var reloaded = new ScrobbleQueue(_dir.Path);
        reloaded.Load();

        Assert.Equal(2, reloaded.Count);
        var entries = reloaded.Peek(10);
        Assert.Equal(["One", "Two"], entries.Select(e => e.Track));
        Assert.Equal("Discovery", entries[0].Album);
        Assert.Equal(320, entries[0].DurationSeconds);
    }

    [Fact]
    public void File_IsVersionedJson()
    {
        var queue = new ScrobbleQueue(_dir.Path);
        queue.Enqueue(Tracks.Entry("One", 100));

        var json = File.ReadAllText(queue.Path);

        Assert.StartsWith("{\"version\":1,", json);
        Assert.Contains("\"scrobbles\":[{\"artist\":\"Daft Punk\",\"track\":\"One\",\"timestamp\":100}]", json);
        Assert.False(File.Exists(queue.Path + ".tmp"));
    }

    [Fact]
    public void SessionEnded_SurvivesARestart_AndAFileWithoutItReadsAsNotEnded()
    {
        var path = System.IO.Path.Combine(_dir.Path, "scrobble-queue.json");
        File.WriteAllText(path, "{\"version\":1,\"scrobbles\":[]}");
        var queue = new ScrobbleQueue(_dir.Path);
        queue.Load();
        Assert.False(queue.SessionEnded);

        queue.SessionEnded = true;
        var reloaded = new ScrobbleQueue(_dir.Path);
        reloaded.Load();
        Assert.True(reloaded.SessionEnded);
        Assert.Contains("\"version\":1", File.ReadAllText(path));

        reloaded.Clear();
        var cleared = new ScrobbleQueue(_dir.Path);
        cleared.Load();
        Assert.False(cleared.SessionEnded);
    }

    [Fact]
    public void OverCapacity_DropsTheOldest()
    {
        var queue = new ScrobbleQueue(_dir.Path);
        for (var i = 0; i < ScrobbleQueue.Capacity + 3; i++)
            queue.Enqueue(Tracks.Entry($"T{i}", i));

        Assert.Equal(ScrobbleQueue.Capacity, queue.Count);
        Assert.Equal("T3", queue.Peek(1)[0].Track);

        var reloaded = new ScrobbleQueue(_dir.Path);
        reloaded.Load();
        Assert.Equal(ScrobbleQueue.Capacity, reloaded.Count);
        Assert.Equal($"T{ScrobbleQueue.Capacity + 2}", reloaded.Peek(ScrobbleQueue.Capacity)[^1].Track);
    }

    [Fact]
    public void RemoveOldest_TakesFromTheFront_AndPersists()
    {
        var queue = new ScrobbleQueue(_dir.Path);
        foreach (var name in new[] { "A", "B", "C" })
            queue.Enqueue(Tracks.Entry(name, 1));

        queue.RemoveOldest(2);

        var reloaded = new ScrobbleQueue(_dir.Path);
        reloaded.Load();
        Assert.Equal("C", Assert.Single(reloaded.Peek(10)).Track);
    }

    [Fact]
    public void MissingFile_IsAnEmptyQueue()
    {
        var queue = new ScrobbleQueue(_dir.Path);
        queue.Load();

        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void CorruptFile_StartsEmpty_AndIsSetAsideNotDestroyed()
    {
        var queue = new ScrobbleQueue(_dir.Path);
        File.WriteAllText(queue.Path, "{\"version\":1,\"scrobbles\":[{\"artist\":");

        queue.Load();

        Assert.Equal(0, queue.Count);
        Assert.False(File.Exists(queue.Path));
        Assert.Equal("{\"version\":1,\"scrobbles\":[{\"artist\":", File.ReadAllText(queue.Path + ".unreadable"));
    }

    [Fact]
    public void NewerVersion_StartsEmpty_AndKeepsTheFileAside()
    {
        var queue = new ScrobbleQueue(_dir.Path);
        const string newer = """{"version":2,"scrobbles":[{"artist":"X","track":"Y","timestamp":1}],"extra":true}""";
        File.WriteAllText(queue.Path, newer);

        queue.Load();
        queue.Enqueue(Tracks.Entry("Mine", 5));

        Assert.Equal(1, queue.Count);
        Assert.Equal(newer, File.ReadAllText(queue.Path + ".unreadable"));
    }

    [Fact]
    public void EntriesMissingArtistOrTrack_AreSkippedOnLoad()
    {
        var queue = new ScrobbleQueue(_dir.Path);
        File.WriteAllText(queue.Path,
            """{"version":1,"scrobbles":[{"artist":"","track":"Y","timestamp":1},{"artist":"A","track":"B","timestamp":2}]}""");

        queue.Load();

        Assert.Equal("B", Assert.Single(queue.Peek(10)).Track);
    }
}
