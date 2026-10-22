using Rambutan.Core.Local;

namespace Rambutan.Core.Tests;

public class LocalTracksTests
{
    private static readonly LocalPaths Windows = new(ignoreCase: true, '\\', '/');
    private static readonly LocalPaths Linux = new(ignoreCase: false, '/');
    private static readonly DateTimeOffset Then = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static LocalFileTags Tags(
        string? title = null, string? artist = null, string? album = null, string? albumArtist = null,
        int? disc = null, int? track = null, double? trackGain = null) =>
        new("flac", TimeSpan.FromSeconds(200), title, artist, album, albumArtist, Genre: null,
            TrackNumber: track, DiscNumber: disc, Year: null, TrackGainDb: trackGain, AlbumGainDb: null);

    private static LocalFile File(string id, string path, LocalFileTags? tags) => new(id, path, 100, Then, tags);

    [Fact]
    public void ATaggedFile_IsATrackUnderItsLocalId()
    {
        var track = LocalTracks.ToTrack(File("local:4", "/m/a.flac", Tags("Song", "Artist", "Album")), Linux);

        Assert.Equal("local:4", track.VideoId);
        Assert.Equal("Song", track.Title);
        Assert.Equal("Artist", track.ArtistDisplay);
        Assert.Null(track.Artists[0].BrowseId);
        Assert.Equal("Album", track.Album);
        Assert.Equal(TimeSpan.FromSeconds(200), track.Duration);
        Assert.Null(track.ThumbnailUrl);
        Assert.Null(track.AlbumBrowseId);
    }

    [Fact]
    public void WithoutTitleOrArtist_TheFileNameIsTheTitle_AndNoArtistIsMadeUp()
    {
        var track = LocalTracks.ToTrack(File("local:1", @"D:\Music\01 Intro.mp3", Tags()), Windows);

        Assert.Equal("01 Intro", track.Title);
        Assert.Empty(track.Artists);
        Assert.Equal("", track.ArtistDisplay);
    }

    [Fact]
    public void WithoutArtist_TheAlbumArtistStandsIn() =>
        Assert.Equal("Band", LocalTracks.ToTrack(File("local:1", "/m/a.mp3", Tags("t", albumArtist: "Band")), Linux).ArtistDisplay);

    [Theory]
    [InlineData("/m/song.flac", "song")]
    [InlineData("/m/my.song.flac", "my.song")]
    [InlineData("/m/.hidden", ".hidden")]
    [InlineData("/m/noext", "noext")]
    public void NameWithoutExtension_OnLinux(string path, string expected) =>
        Assert.Equal(expected, Linux.NameWithoutExtension(path));

    [Fact]
    public void NameWithoutExtension_OnWindows_TakesEitherSeparator()
    {
        Assert.Equal("b", Windows.NameWithoutExtension(@"C:\x/y\b.mp3"));
        Assert.Equal("b", Windows.NameWithoutExtension("C:/x/b.mp3"));
    }

    [Fact]
    public void ABackslash_IsPartOfALinuxFileName() =>
        Assert.Equal(@"a\b", Linux.NameWithoutExtension(@"/m/a\b.mp3"));

    [Theory]
    // -18 LUFS (gain 0) is 4 dB below YouTube's -14 LUFS reference.
    [InlineData(0.0, -4.0)]
    // A loud master: -6.2 dB of gain means -11.8 LUFS, 2.2 dB above the reference.
    [InlineData(-6.2, 2.2)]
    [InlineData(3.0, -7.0)]
    public void ReplayGain_BecomesLoudnessAboveYouTubesReference(double gain, double loudness) =>
        Assert.Equal(loudness, LocalTracks.LoudnessDb(Tags(trackGain: gain))!.Value, precision: 6);

    [Fact]
    public void WithoutATrackGain_ThereIsNoLoudness()
    {
        Assert.Null(LocalTracks.LoudnessDb(Tags()));
        Assert.Null(LocalTracks.LoudnessDb(null));
    }

    [Fact]
    public void LibraryOrder_IsArtistAlbumDiscTrack_ThenTitle_WithUntaggedLast()
    {
        var files = new[]
        {
            File("local:1", "/m/z.mp3", Tags("Zed")),
            File("local:2", "/m/b2.mp3", Tags("B two", "beta", "Album", disc: 1, track: 2)),
            File("local:3", "/m/a.mp3", Tags("A", "Alpha", "Second")),
            File("local:4", "/m/b1.mp3", Tags("B one", "Beta", "Album", disc: 1, track: 1)),
            File("local:5", "/m/b3.mp3", Tags("B disc two", "Beta", "Album", disc: 2, track: 1)),
            File("local:6", "/m/a0.mp3", Tags("A first", "alpha", "First")),
            File("local:7", "/m/a.flac", null),
        };

        var order = LocalTracks.InLibraryOrder(files, Linux).Select(f => f.Id);

        Assert.Equal(["local:6", "local:3", "local:4", "local:2", "local:5", "local:7", "local:1"], order);
    }
}
