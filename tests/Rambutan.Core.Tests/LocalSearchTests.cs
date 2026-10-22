using Rambutan.Core.Local;

namespace Rambutan.Core.Tests;

public class LocalSearchTests
{
    private static readonly LocalPaths Linux = new(ignoreCase: false, '/');
    private static int _next;

    private static LocalFile Song(string? title, string? artist = null, string? album = null,
        string? albumArtist = null, int? track = null, string? path = null, bool missing = false) =>
        new($"local:{++_next}", path ?? $"/m/{_next}.flac", 100, DateTimeOffset.UnixEpoch,
            new LocalFileTags("flac", Title: title, Artist: artist, Album: album, AlbumArtist: albumArtist,
                TrackNumber: track),
            IsMissing: missing);

    private static string?[] Titles(LocalMatches m) => m.Files.Select(f => f.Tags?.Title).ToArray();

    [Fact]
    public void EveryWord_HasToMatch_SomewhereInTheTags()
    {
        var files = new[]
        {
            Song("Windowlicker", "Aphex Twin", "Windowlicker EP"),
            Song("Avril 14th", "Aphex Twin", "Drukqs"),
            Song("Teardrop", "Massive Attack", "Mezzanine"),
        };

        Assert.Equal(["Avril 14th"], Titles(LocalSearch.Find(files, "aphex drukqs", 10, Linux)));
        Assert.Equal(2, LocalSearch.Find(files, "APHEX", 10, Linux).Total);
        Assert.Empty(LocalSearch.Find(files, "aphex mezzanine", 10, Linux).Files);
    }

    [Fact]
    public void CaseAndAccents_DontMatter_EitherWay()
    {
        var files = new[] { Song("Café del Mar", "Énergie"), Song("Hyperballad", "Björk") };

        Assert.Equal(["Café del Mar"], Titles(LocalSearch.Find(files, "cafe  ENERGIE", 10, Linux)));
        Assert.Equal(["Hyperballad"], Titles(LocalSearch.Find(files, "bjork", 10, Linux)));
        Assert.Equal(["Hyperballad"], Titles(LocalSearch.Find(files, "Björk", 10, Linux)));
    }

    [Fact]
    public void AnUntitledFile_IsFoundByItsFileName() =>
        Assert.Single(LocalSearch.Find([Song(null, path: "/m/03 Night Drive.mp3")], "night drive", 10, Linux).Files);

    [Fact]
    public void MissingAndUnreadableFiles_AreLeftOut()
    {
        var unreadable = new LocalFile("local:900", "/m/song.mp3", 1, DateTimeOffset.UnixEpoch, Tags: null);
        var files = new[] { Song("song", missing: true), unreadable };

        Assert.Equal(0, LocalSearch.Find(files, "song", 10, Linux).Total);
    }

    [Fact]
    public void TheTitle_RanksAboveTheArtistAndAlbum_ThenTheLibraryOrder()
    {
        var files = new[]
        {
            Song("Something", "Zed", "Blue"),        // "blue" only in the album
            Song("Blue Monday", "New Order"),          // starts with it
            Song("Blue", "Joni Mitchell"),             // equals it
            Song("Into the Blue", "Moby"),             // holds it
            Song("Ocean", "Alpha", "Blue Lines"),      // album again, library order first
        };

        Assert.Equal(["Blue", "Blue Monday", "Into the Blue", "Ocean", "Something"],
            Titles(LocalSearch.Find(files, "blue", 10, Linux)));
    }

    [Fact]
    public void TheLimit_CapsTheFiles_NotTheTotal()
    {
        var files = Enumerable.Range(1, 30).Select(n => Song($"Track {n}", "Band", track: n)).ToList();

        var found = LocalSearch.Find(files, "track", 5, Linux);

        Assert.Equal(30, found.Total);
        Assert.Equal(["Track 1", "Track 2", "Track 3", "Track 4", "Track 5"], Titles(found));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankQuery_FindsNothing(string query) =>
        Assert.Same(LocalMatches.None, LocalSearch.Find([Song("x")], query, 10, Linux));
}
