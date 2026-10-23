using Rambutan.Core.Models;
using Rambutan.Core.Playlists;

namespace Rambutan.LocalLibrary.Tests;

public sealed class LocalPlaylistStoreTests : IDisposable
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

    private string FilePath => Path.Combine(_dir, "local-playlists.json");

    [Fact]
    public void RoundTrips_EveryField()
    {
        var created = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.FromHours(1));
        var song = new TrackInfo
        {
            VideoId = "YQHsXMglC9A", Title = "Hello", Artists = [new ArtistRef("Adele", "UCadele")],
            Album = "25", AlbumBrowseId = "MPREb_25", Duration = TimeSpan.FromSeconds(296),
            ThumbnailUrl = "https://lh3/hello", TrackNumber = 1, SongId = "khnokW3Mw24",
        };
        var file = new TrackInfo
        {
            VideoId = "local:4", Title = "Mine", Artists = [new ArtistRef("Me", null)], Album = "Tapes",
            Duration = TimeSpan.FromSeconds(61.5),
        };
        var playlist = new LocalPlaylist("p1", "Mix", [new(song), new(file, "/music/mine.flac"), new(song)],
            created, created.AddDays(1), "PLmix");

        new LocalPlaylistStore(_dir).Save([playlist, new LocalPlaylist("p2", "Empty", [], created, created)]);
        var loaded = new LocalPlaylistStore(_dir).Load();

        Assert.Equal(LocalPlaylistsProblem.None, loaded.Problem);
        Assert.Equal(2, loaded.Playlists.Count);
        var back = loaded.Playlists[0];
        Assert.Equal(("p1", "Mix", created, created.AddDays(1), "PLmix"),
            (back.Id, back.Name, back.CreatedAt, back.ChangedAt, back.CopiedFrom));
        Assert.Equal(3, back.Items.Count);
        Assert.Equal(song with { Artists = back.Items[0].Track.Artists }, back.Items[0].Track);
        Assert.Equal([new ArtistRef("Adele", "UCadele")], back.Items[0].Track.Artists);
        Assert.Null(back.Items[0].Path);
        Assert.Equal(file with { Artists = back.Items[1].Track.Artists }, back.Items[1].Track);
        Assert.Equal("/music/mine.flac", back.Items[1].Path);
        Assert.Null(loaded.Playlists[1].CopiedFrom);
        Assert.Empty(loaded.Playlists[1].Items);
    }

    [Fact]
    public void NoFile_LoadsEmpty()
    {
        var loaded = new LocalPlaylistStore(_dir).Load();

        Assert.Empty(loaded.Playlists);
        Assert.Equal(LocalPlaylistsProblem.None, loaded.Problem);
    }

    [Fact]
    public void WritesVersion1()
    {
        new LocalPlaylistStore(_dir).Save([]);

        Assert.Contains("\"version\":1", File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void UnreadableFile_IsSetAside_AndLoadsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");

        var loaded = new LocalPlaylistStore(_dir).Load();

        Assert.Empty(loaded.Playlists);
        Assert.Equal(LocalPlaylistsProblem.Unreadable, loaded.Problem);
        Assert.Equal(FilePath + ".unreadable", loaded.SetAside);
        Assert.False(File.Exists(FilePath));
        Assert.Equal("{ not json", File.ReadAllText(FilePath + ".unreadable"));
    }

    [Fact]
    public void UnreadableFile_ThatCantBeMoved_IsLeftInItsPlace()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows refuses to move a file that is open");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");

        LoadedLocalPlaylists loaded;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            loaded = new LocalPlaylistStore(_dir).Load();

        Assert.Equal(LocalPlaylistsProblem.Unreadable, loaded.Problem);
        Assert.Null(loaded.SetAside);
        Assert.Equal("{ not json", File.ReadAllText(FilePath));
    }

    [Fact]
    public void NewerFile_IsLeftAsItIs()
    {
        Directory.CreateDirectory(_dir);
        const string newer = """{"version":2,"playlists":[{"id":"p","name":"Future"}]}""";
        File.WriteAllText(FilePath, newer);

        var loaded = new LocalPlaylistStore(_dir).Load();

        Assert.Empty(loaded.Playlists);
        Assert.Equal(LocalPlaylistsProblem.Newer, loaded.Problem);
        Assert.Equal(newer, File.ReadAllText(FilePath));
    }

    [Fact]
    public void InvalidRows_AreDropped_NotThePlaylist()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """
            {"version":1,"playlists":[
              {"id":"p","name":" Mix ","items":[{"videoId":"a","title":"A"},{"videoId":"b"},null,{"title":"C"}]},
              {"id":"","name":"No id"},
              {"id":"q","name":"  "},
              null
            ]}
            """);

        var loaded = new LocalPlaylistStore(_dir).Load();

        var playlist = Assert.Single(loaded.Playlists);
        Assert.Equal("Mix", playlist.Name);
        Assert.Equal(["a"], playlist.Items.Select(i => i.Track.VideoId));
    }

    [Fact]
    public void AFailedSave_Throws()
    {
        Directory.CreateDirectory(_dir);
        // A folder where the file goes can't be replaced by a file.
        Directory.CreateDirectory(FilePath);

        Assert.ThrowsAny<Exception>(() => new LocalPlaylistStore(_dir).Save([]));
    }
}
