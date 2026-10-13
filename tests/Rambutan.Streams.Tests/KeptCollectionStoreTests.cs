using Rambutan.Core.Downloads;
using Rambutan.Core.Models;

namespace Rambutan.Streams.Tests;

public sealed class KeptCollectionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string FilePath => Path.Combine(_dir, "downloads-collections.json");

    [Fact]
    public void RoundTrips_EveryField()
    {
        var at = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.FromHours(1));
        var track = new TrackInfo
        {
            VideoId = "YQHsXMglC9A", Title = "Hello", Artists = [new ArtistRef("Adele", "UCadele")],
            Album = "25", AlbumBrowseId = "MPREb_25", Duration = TimeSpan.FromSeconds(296),
            ThumbnailUrl = "https://lh3/hello", TrackNumber = 1, SongId = "khnokW3Mw24",
        };
        var copy = new KeptCollection(KeptCollectionKind.Album, "MPREb_25", "25", "Adele", "UCadele",
            "Album • 2015", "https://lh3/25", [track], at);

        new KeptCollectionStore(_dir).Save([copy]);
        var loaded = Assert.Single(new KeptCollectionStore(_dir).Load());

        Assert.Equal(copy with { Tracks = [] }, loaded with { Tracks = [] });
        var back = Assert.Single(loaded.Tracks);
        Assert.Equal(track with { Artists = [] }, back with { Artists = [] });
        Assert.Equal(track.Artists, back.Artists);
        Assert.Contains("\"version\":1", File.ReadAllText(FilePath));
    }

    [Fact]
    public void NoFile_IsEmpty() => Assert.Empty(new KeptCollectionStore(_dir).Load());

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":99,\"collections\":[]}")]
    public void AnUnreadableFile_IsSetAside_AndReadsAsEmpty(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, content);

        Assert.Empty(new KeptCollectionStore(_dir).Load());
        Assert.False(File.Exists(FilePath));
        Assert.Equal(content, File.ReadAllText(FilePath + ".unreadable"));
    }

    [Fact]
    public void EntriesItCantUse_AreSkipped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """
            {"version":1,"collections":[
              {"kind":"album","id":"ok","title":"Ok","tracks":[{"videoId":"v","title":"V"}]},
              {"kind":"mixtape","id":"x","title":"X","tracks":[{"videoId":"v","title":"V"}]},
              {"kind":"playlist","id":"","title":"No id","tracks":[{"videoId":"v","title":"V"}]},
              {"kind":"playlist","id":"empty","title":"No usable tracks","tracks":[{"videoId":"v"}]}
            ]}
            """);

        Assert.Equal(["ok"], new KeptCollectionStore(_dir).Load().Select(c => c.Id));
    }

    [Theory]
    [InlineData("""{"videoId":"v","title":"V"}""")]
    [InlineData("""{"videoId":"v","title":"V","songId":""}""")]
    public void A_track_saved_without_a_SongId_reads_as_null(string track)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, $$"""{"version":1,"collections":[{"kind":"album","id":"a","title":"A","tracks":[{{track}}]}]}""");

        var loaded = Assert.Single(new KeptCollectionStore(_dir).Load());

        Assert.Null(Assert.Single(loaded.Tracks).SongId);
    }
}
