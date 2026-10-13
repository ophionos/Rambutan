using Rambutan.App.Services;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

public sealed class QueueStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-queue-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private QueueStore Store() => new(_dir);

    private static TrackInfo Track(string id) => new()
    {
        VideoId = id,
        Title = "Title " + id,
        Artists = [new("Daft Punk", "UCdp"), new("Julian Casablancas")],
        Album = "Random Access Memories",
        Duration = TimeSpan.FromSeconds(338),
        ThumbnailUrl = $"https://example.invalid/{id}.jpg",
        AlbumBrowseId = "MPREb_" + id,
    };

    [Fact]
    public void RoundTrip_KeepsTracksPositionAndRepeat()
    {
        var saved = new QueueSnapshot([Track("a"), Track("b"), Track("c")], 1, RepeatMode.All);

        Store().Save(saved);
        var loaded = Store().Load();

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded.Tracks.Count);
        for (var i = 0; i < 3; i++)
        {
            var (before, after) = (saved.Tracks[i], loaded.Tracks[i]);
            Assert.Equal(before.VideoId, after.VideoId);
            Assert.Equal(before.Title, after.Title);
            Assert.Equal(before.Artists, after.Artists); // ArtistRef is a record
            Assert.Equal(before.Album, after.Album);
            Assert.Equal(before.Duration, after.Duration);
            Assert.Equal(before.ThumbnailUrl, after.ThumbnailUrl);
            Assert.Equal(before.AlbumBrowseId, after.AlbumBrowseId); // the title link survives
        }
        Assert.Equal(1, loaded.CurrentIndex);
        Assert.Equal(RepeatMode.All, loaded.Repeat);
    }

    [Fact]
    public void SongId_RoundTrips_AndIsOptionalWithinVersion1()
    {
        var albumRow = Track("a5uQMwRMHcs") with { SongId = "khnokW3Mw24" };
        Store().Save(new QueueSnapshot([albumRow, Track("b")], 0, RepeatMode.Off));

        var loaded = Store().Load()!;
        var json = File.ReadAllText(Store().Path);

        Assert.Equal("khnokW3Mw24", loaded.Tracks[0].SongId);
        Assert.Null(loaded.Tracks[1].SongId);
        Assert.Contains("\"version\":1", json);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"songId\""));

        // What earlier builds wrote: no songId reads as unknown.
        File.WriteAllText(Store().Path, """{"version":1,"currentIndex":0,"tracks":[{"videoId":"a","title":"A","songId":""}]}""");
        Assert.Null(Store().Load()!.Tracks[0].SongId);
    }

    [Fact]
    public void ListedVideoId_RoundTrips_AndIsOptionalWithinVersion1()
    {
        // An album row restored from the queue still falls back to its video.
        var albumRow = AlbumAudio.Prefer(Track("a5uQMwRMHcs") with { SongId = "khnokW3Mw24" });
        Store().Save(new QueueSnapshot([albumRow, Track("b")], 0, RepeatMode.Off));

        var loaded = Store().Load()!;

        Assert.Equal("khnokW3Mw24", loaded.Tracks[0].VideoId);
        Assert.Equal("a5uQMwRMHcs", loaded.Tracks[0].ListedVideoId);
        Assert.Null(loaded.Tracks[1].ListedVideoId);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Store().Path), "\"listedVideoId\""));

        File.WriteAllText(Store().Path, """{"version":1,"currentIndex":0,"tracks":[{"videoId":"a","title":"A","listedVideoId":""}]}""");
        Assert.Null(Store().Load()!.Tracks[0].ListedVideoId);
    }

    [Fact]
    public void TheFileIsVersioned()
    {
        Store().Save(new QueueSnapshot([Track("a")], 0, RepeatMode.Off));

        var json = File.ReadAllText(Store().Path);

        Assert.Contains("\"version\":1", json);
    }

    [Fact]
    public void NoFile_MeansNoSavedQueue() => Assert.Null(Store().Load());

    [Theory]
    [InlineData("not json at all {")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"version\":1,\"tracks\":\"nope\"}")]
    public void AGarbageFile_MeansNoSavedQueue(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Store().Path, content);

        Assert.Null(Store().Load());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void AnUnknownVersion_IsNotRead(int version)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Store().Path,
            $$"""{"version":{{version}},"currentIndex":0,"tracks":[{"videoId":"a","title":"A"}]}""");

        Assert.Null(Store().Load());
    }

    [Fact]
    public void BrokenTracks_AreDropped_AndTheIndexFollowsItsTrack()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Store().Path, """
            {"version":1,"currentIndex":3,"repeat":"sideways","tracks":[
              {"videoId":"a","title":"A"},
              {"title":"no id"},
              {"videoId":"c"},
              {"videoId":"d","title":"D","artists":[{"name":"X"},{"browseId":"nameless"}],"durationSeconds":-1}
            ]}
            """);

        var loaded = Store().Load();

        Assert.NotNull(loaded);
        Assert.Equal(["a", "d"], loaded.Tracks.Select(t => t.VideoId));
        Assert.Equal(1, loaded.CurrentIndex);
        Assert.Equal(RepeatMode.Off, loaded.Repeat);
        Assert.Equal(["X"], loaded.Tracks[1].Artists.Select(a => a.Name));
        Assert.Null(loaded.Tracks[1].Duration);
    }

    [Fact]
    public void Save_ReplacesThePreviousFile()
    {
        Store().Save(new QueueSnapshot([Track("a"), Track("b")], 1, RepeatMode.Off));
        Store().Save(new QueueSnapshot([Track("z")], 0, RepeatMode.One));

        var loaded = Store().Load()!;

        Assert.Equal(["z"], loaded.Tracks.Select(t => t.VideoId));
        Assert.False(File.Exists(Store().Path + ".tmp"));
    }

    [Fact]
    public void AutoplayMarks_RoundTrip_AndFollowTheirTracksPastDroppedOnes()
    {
        Store().Save(new QueueSnapshot([Track("a"), Track("b"), Track("c")], 0, RepeatMode.Off, null, [1, 2]));
        Assert.Equal([1, 2], Store().Load()!.AutoplayIndices!);

        File.WriteAllText(Store().Path, """
            {"version":1,"currentIndex":0,"tracks":[
              {"videoId":"a","title":"A"},{"title":"broken"},{"videoId":"c","title":"C"}
            ],"autoplay":[1,2]}
            """);
        Assert.Equal([1], Store().Load()!.AutoplayIndices!);
    }

    [Fact]
    public void Shuffle_RoundTrips_WithItsOriginalOrder()
    {
        Store().Save(new QueueSnapshot([Track("c"), Track("a"), Track("b")], 1, RepeatMode.Off, [2, 0, 1]));

        var loaded = Store().Load()!;

        Assert.Equal(["c", "a", "b"], loaded.Tracks.Select(t => t.VideoId));
        Assert.Equal([2, 0, 1], loaded.ShuffleRanks!);
    }

    [Fact]
    public void ShuffleOff_WritesNoRanks()
    {
        Store().Save(new QueueSnapshot([Track("a")], 0, RepeatMode.Off));

        Assert.DoesNotContain("shuffleRanks", File.ReadAllText(Store().Path));
        Assert.False(Store().Load()!.Shuffle);
    }

    [Fact]
    public void AFileWithoutShuffle_ReadsAsShuffleOff()
    {
        // What earlier builds wrote before shuffle was persisted.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Store().Path, """{"version":1,"currentIndex":0,"tracks":[{"videoId":"a","title":"A"}]}""");

        Assert.False(Store().Load()!.Shuffle);
    }

    [Fact]
    public void DroppedTracks_TakeTheirRanksWithThem()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Store().Path, """
            {"version":1,"currentIndex":0,"shuffleRanks":[3,0,2,1],"tracks":[
              {"videoId":"d","title":"D"},
              {"videoId":"a","title":"A"},
              {"title":"no id"},
              {"videoId":"b","title":"B"}
            ]}
            """);

        var loaded = Store().Load()!;

        Assert.Equal(["d", "a", "b"], loaded.Tracks.Select(t => t.VideoId));
        Assert.Equal([2, 0, 1], loaded.ShuffleRanks!); // a, b, d in original order
    }

    [Theory]
    [InlineData("[0,0]")]
    [InlineData("[0]")]
    [InlineData("[0,5]")]
    public void RanksThatDontFit_ReadAsShuffleOff(string ranks)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Store().Path,
            "{\"version\":1,\"currentIndex\":0,\"shuffleRanks\":" + ranks +
            ",\"tracks\":[{\"videoId\":\"a\",\"title\":\"A\"},{\"videoId\":\"b\",\"title\":\"B\"}]}");

        var loaded = Store().Load()!;

        Assert.False(loaded.Shuffle);
        Assert.Equal(["a", "b"], loaded.Tracks.Select(t => t.VideoId));
    }
}
