using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.Core.Playlists;

namespace Rambutan.Core.Tests;

public class M3uTests
{
    private static readonly LocalPaths Windows = LocalPaths.Windows;
    private static readonly LocalPaths Unix = LocalPaths.Unix;

    [Fact]
    public void Parse_KeepsTheEntriesInOrder_WithTheirInfo_AndSkipsComments()
    {
        var text = "\uFEFF#EXTM3U\r\n#EXTINF:215,Bonnie Tyler - Total Eclipse\r\nmusic\\a.mp3\r\n\r\n" +
                   "# a comment\n#EXTINF:-1 tvg-id=\"x\",Untimed\nb.flac\nc.ogg\nmusic\\a.mp3\n";

        var entries = M3u.Parse(text);

        Assert.Equal(
            [
                new M3uEntry(@"music\a.mp3", "Bonnie Tyler - Total Eclipse", TimeSpan.FromSeconds(215)),
                new M3uEntry("b.flac", "Untimed"),
                new M3uEntry("c.ogg"),
                new M3uEntry(@"music\a.mp3"),
            ],
            entries);
    }

    [Theory]
    [InlineData(@"a.mp3", @"C:\Lists", @"C:\Lists\a.mp3")]
    [InlineData(@"..\Music\a.mp3", @"C:\Lists", @"C:\Music\a.mp3")]
    [InlineData(@"./sub/../a.mp3", @"C:\Lists", @"C:\Lists\a.mp3")]
    [InlineData(@"..\..\..\a.mp3", @"C:\Lists", @"C:\a.mp3")]
    [InlineData(@"D:\Music\a.mp3", @"C:\Lists", @"D:\Music\a.mp3")]
    [InlineData(@"d:/Music/a.mp3", @"C:\Lists", @"d:\Music\a.mp3")]
    [InlineData(@"\Music\a.mp3", @"D:\Lists", @"D:\Music\a.mp3")]
    [InlineData(@"\\nas\share\Music\a.mp3", @"C:\Lists", @"\\nas\share\Music\a.mp3")]
    [InlineData(@"a.mp3", @"\\nas\share\Lists", @"\\nas\share\Lists\a.mp3")]
    public void Locate_OnWindows_MakesEveryPathFull(string location, string folder, string expected) =>
        Assert.Equal(expected, M3u.Locate(location, folder, Windows));

    [Theory]
    [InlineData("a.mp3", "/home/me/Lists", "/home/me/Lists/a.mp3")]
    [InlineData("../Music/a.mp3", "/home/me/Lists", "/home/me/Music/a.mp3")]
    [InlineData("/srv/Music/a.mp3", "/home/me/Lists", "/srv/Music/a.mp3")]
    [InlineData(@"..\Music\a.mp3", "/home/me/Lists", "/home/me/Music/a.mp3")]
    [InlineData(@"odd\name.mp3", "/home/me/Lists", "/home/me/Lists/odd/name.mp3")]
    [InlineData(@"dir/odd\name.mp3", "/home/me/Lists", @"/home/me/Lists/dir/odd\name.mp3")]
    public void Locate_ElsewhereTakesWindowsSeparators_OnlyWhenThereIsNoOther(string location, string folder, string expected) =>
        Assert.Equal(expected, M3u.Locate(location, folder, Unix));

    [Theory]
    [InlineData("https://music.youtube.com/watch?v=abc")]
    [InlineData("http://radio.example/stream.mp3")]
    [InlineData("rtsp://cam/feed")]
    public void Locate_HasNoPathForAUrl(string location) =>
        Assert.Null(M3u.Locate(location, @"C:\Lists", Windows));

    [Fact]
    public void Locate_TakesAFileUrl_AsThePathItNames()
    {
        if (OperatingSystem.IsWindows())
            Assert.Equal(@"C:\Music\a b.mp3", M3u.Locate("file:///C:/Music/a%20b.mp3", @"D:\Lists", Windows));
        else
            Assert.Equal("/home/me/a b.mp3", M3u.Locate("file:///home/me/a%20b.mp3", "/srv", Unix));
    }

    [Theory]
    [InlineData(@"C:\Lists", @"C:\Lists\a.mp3", @"a.mp3")]
    [InlineData(@"C:\Lists", @"C:\Music\Album\a.mp3", @"..\Music\Album\a.mp3")]
    [InlineData(@"C:\Lists\", @"c:\lists\Sub\a.mp3", @"Sub\a.mp3")]
    [InlineData(@"C:\Lists", @"D:\Music\a.mp3", @"D:\Music\a.mp3")]
    [InlineData(@"C:\", @"C:\a.mp3", @"a.mp3")]
    public void Relative_OnWindows_ClimbsWithinADrive_AndNotAcross(string folder, string path, string expected) =>
        Assert.Equal(expected, Windows.Relative(folder, path));

    [Theory]
    [InlineData("/home/me/Lists", "/home/me/Music/a.mp3", "../Music/a.mp3")]
    [InlineData("/home/me/Lists", "/home/me/lists/a.mp3", "../lists/a.mp3")]
    [InlineData("/", "/a.mp3", "a.mp3")]
    public void Relative_Elsewhere_ComparesWithCase(string folder, string path, string expected) =>
        Assert.Equal(expected, Unix.Relative(folder, path));

    private static LocalPlaylistItem File(string path, string title, string? artist = null, int? seconds = null) =>
        new(new TrackInfo
        {
            VideoId = "local:1", Title = title,
            Artists = artist is null ? [] : [new ArtistRef(artist, null)],
            Duration = seconds is null ? null : TimeSpan.FromSeconds(seconds.Value),
        }, path);

    private static LocalPlaylistItem Song(string id) =>
        new(new TrackInfo { VideoId = id, Title = id, Artists = [] });

    [Fact]
    public void Write_ListsTheFiles_RelativeWherePossible_AndLeavesOutTheSongs()
    {
        var (text, written, leftOut) = M3u.Write(
        [
            File(@"C:\Music\a.mp3", "Total Eclipse", "Bonnie Tyler", 334),
            Song("dQw4w9WgXcQ"),
            File(@"D:\b.flac", "Line\nbreak"),
            File(@"C:\Music\a.mp3", "Total Eclipse", "Bonnie Tyler", 334),
        ], @"C:\Lists", Windows);

        Assert.Equal(
            "#EXTM3U\n" +
            "#EXTINF:334,Bonnie Tyler - Total Eclipse\n..\\Music\\a.mp3\n" +
            "#EXTINF:-1,Line break\nD:\\b.flac\n" +
            "#EXTINF:334,Bonnie Tyler - Total Eclipse\n..\\Music\\a.mp3\n",
            text);
        Assert.Equal(3, written);
        Assert.Equal(1, leftOut);
    }

    [Fact]
    public void AWrittenPlaylist_ReadsBackToTheSamePaths()
    {
        var (text, _, _) = M3u.Write([File(@"C:\Music\a.mp3", "A"), File(@"E:\b.mp3", "B")], @"C:\Lists", Windows);

        var paths = M3u.Parse(text).Select(e => M3u.Locate(e.Location, @"C:\Lists", Windows));

        Assert.Equal([@"C:\Music\a.mp3", @"E:\b.mp3"], paths);
    }
}
