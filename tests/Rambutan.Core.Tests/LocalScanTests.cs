using Rambutan.Core.Local;
using Rambutan.Core.Models;

namespace Rambutan.Core.Tests;

public class LocalScanTests
{
    private static readonly LocalPaths Windows = new(ignoreCase: true, '\\', '/');
    private static readonly LocalPaths Linux = new(ignoreCase: false, '/');
    private static readonly DateTimeOffset Then = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static FoundFile Found(string path, long size = 100, DateTimeOffset? at = null) => new(path, size, at ?? Then);

    private static KnownFile Known(string id, string path, long size = 100, DateTimeOffset? at = null, bool missing = false) =>
        new(id, path, size, at ?? Then, missing);

    [Fact]
    public void NewFiles_GetIdsInOrder_FromTheNextNumber()
    {
        var plan = LocalScan.Plan([], [Found("/m/a.mp3"), Found("/m/b.flac")], ["/m"], Linux, nextId: 7);

        Assert.Equal(["local:7", "local:8"], plan.ToRead.Select(f => f.Id));
        Assert.All(plan.ToRead, f => Assert.True(f.IsNew));
        Assert.Equal(9, plan.NextId);
    }

    [Fact]
    public void AssignedIds_AreNeverYouTubeIds()
    {
        var plan = LocalScan.Plan([], [Found("/m/a.mp3")], ["/m"], Linux, nextId: 1);

        Assert.False(YouTubeIds.IsVideoId(plan.ToRead[0].Id));
        Assert.True(LocalIds.IsLocal(plan.ToRead[0].Id));
    }

    [Fact]
    public void AnUnchangedFile_IsNotReadAgain()
    {
        var plan = LocalScan.Plan([Known("local:1", "/m/a.mp3")], [Found("/m/a.mp3")], ["/m"], Linux, nextId: 2);

        Assert.Empty(plan.ToRead);
        Assert.Empty(plan.NowMissing);
        Assert.Equal(1, plan.Unchanged);
        Assert.Equal(2, plan.NextId);
    }

    [Fact]
    public void AChangedFile_IsReadAgain_AndKeepsItsId()
    {
        var plan = LocalScan.Plan(
            [Known("local:1", "/m/a.mp3"), Known("local:2", "/m/b.mp3")],
            [Found("/m/a.mp3", size: 200), Found("/m/b.mp3", at: Then.AddSeconds(1))],
            ["/m"], Linux, nextId: 3);

        Assert.Equal(["local:1", "local:2"], plan.ToRead.Select(f => f.Id));
        Assert.All(plan.ToRead, f => Assert.False(f.IsNew));
        Assert.Equal(3, plan.NextId);
    }

    [Fact]
    public void AFileThatIsGone_IsMissing_AndKeepsItsId_WhenItComesBack()
    {
        var gone = LocalScan.Plan([Known("local:1", "/m/a.mp3")], [], ["/m"], Linux, nextId: 2);
        var back = LocalScan.Plan([Known("local:1", "/m/a.mp3", missing: true)], [Found("/m/a.mp3")], ["/m"], Linux, nextId: 2);

        Assert.Equal(["local:1"], gone.NowMissing);
        Assert.Equal([new FileToRead(Found("/m/a.mp3"), "local:1", IsNew: false)], back.ToRead);
    }

    [Fact]
    public void AFileAlreadyMissing_IsNotReportedAgain()
    {
        var plan = LocalScan.Plan([Known("local:1", "/m/a.mp3", missing: true)], [], ["/m"], Linux, nextId: 2);

        Assert.Empty(plan.NowMissing);
    }

    [Fact]
    public void FilesOfAFolderThatWasNotScanned_StayAsTheyWere()
    {
        // An unplugged drive: its folder is left out of the scanned roots.
        var plan = LocalScan.Plan(
            [Known("local:1", "/mnt/usb/a.mp3"), Known("local:2", "/m/b.mp3")],
            [], ["/m"], Linux, nextId: 3);

        Assert.Equal(["local:2"], plan.NowMissing);
    }

    [Fact]
    public void AMovedFile_IsANewFile_AndTheOldOneIsMissing()
    {
        var plan = LocalScan.Plan([Known("local:1", "/m/a.mp3")], [Found("/m/x/a.mp3")], ["/m"], Linux, nextId: 2);

        Assert.Equal(["local:1"], plan.NowMissing);
        Assert.Equal("local:2", Assert.Single(plan.ToRead).Id);
    }

    [Fact]
    public void AFileFoundThroughTwoWatchedFolders_CountsOnce()
    {
        var plan = LocalScan.Plan([], [Found("/m/a.mp3"), Found("/m/a.mp3")], ["/m", "/m"], Linux, nextId: 1);

        Assert.Single(plan.ToRead);
        Assert.Equal(2, plan.NextId);
    }

    [Fact]
    public void OnWindows_PathsCompareWithoutCase()
    {
        var plan = LocalScan.Plan(
            [Known("local:1", @"C:\Music\A.mp3")], [Found(@"c:\music\a.MP3")], [@"C:\MUSIC"], Windows, nextId: 2);

        Assert.Equal(1, plan.Unchanged);
        Assert.Empty(plan.NowMissing);
    }

    [Fact]
    public void OnLinux_PathsDifferingInCase_AreTwoFiles()
    {
        var plan = LocalScan.Plan([Known("local:1", "/m/A.mp3")], [Found("/m/a.mp3")], ["/m"], Linux, nextId: 2);

        Assert.Equal("local:2", Assert.Single(plan.ToRead).Id);
        Assert.Equal(["local:1"], plan.NowMissing);
    }

    [Theory]
    [InlineData(@"C:\Music\a.mp3", @"C:\Music", true)]
    [InlineData(@"C:\Music\a.mp3", @"C:\Music\", true)]
    [InlineData(@"C:\Music\x\a.mp3", @"c:\music", true)]
    [InlineData(@"C:\Music", @"C:\Music", true)]
    [InlineData(@"C:\Music2\a.mp3", @"C:\Music", false)]
    [InlineData(@"D:\a.mp3", @"D:\", true)]
    public void IsUnder_OnWindows(string path, string root, bool under) =>
        Assert.Equal(under, Windows.IsUnder(path, root));

    [Theory]
    [InlineData("/m/a.mp3", "/m", true)]
    [InlineData("/m/a.mp3", "/m/", true)]
    [InlineData("/m2/a.mp3", "/m", false)]
    [InlineData("/M/a.mp3", "/m", false)]
    [InlineData("/a.mp3", "/", true)]
    public void IsUnder_OnLinux(string path, string root, bool under) =>
        Assert.Equal(under, Linux.IsUnder(path, root));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(6, 3)]
    [InlineData(8, 4)]
    [InlineData(32, 4)]
    public void ReadThreads_AreHalfTheCores_AtMostFour(int cores, int threads) =>
        Assert.Equal(threads, LocalScan.ReadThreads(cores));
}

public class LocalTagValuesTests
{
    [Theory]
    [InlineData("3", 3)]
    [InlineData("3/12", 3)]
    [InlineData(" 03 / 12 ", 3)]
    [InlineData("0", null)]
    [InlineData("A1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Position(string? text, int? expected) => Assert.Equal(expected, LocalTagValues.Position(text));

    [Theory]
    [InlineData("2019", 2019)]
    [InlineData("2019-04-01", 2019)]
    [InlineData("2019-04-01T00:00:00Z", 2019)]
    [InlineData("20190401", null)]
    [InlineData("19", null)]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void Year(string? text, int? expected) => Assert.Equal(expected, LocalTagValues.Year(text));

    [Theory]
    [InlineData("lyrics", true)]
    [InlineData("LYRICS", true)]
    [InlineData("lyrics-eng", true)]
    [InlineData("lyrics-Verse-eng", true)]
    [InlineData("UNSYNCEDLYRICS", true)]
    [InlineData("SyncedLyrics", true)]
    [InlineData("LYRICIST", false)]
    [InlineData("lyricsx", false)]
    [InlineData("comment", false)]
    public void IsLyricsKey(string key, bool expected) => Assert.Equal(expected, LocalTagValues.IsLyricsKey(key));

    [Theory]
    [InlineData("-6.20 dB", -6.2)]
    [InlineData("+1.5 dB", 1.5)]
    [InlineData("-6.2", -6.2)]
    [InlineData("-6.2dB", -6.2)]
    [InlineData("-6,2 dB", null)]
    [InlineData("-100 dB", null)]
    [InlineData("NaN", null)]
    [InlineData(null, null)]
    public void GainDb(string? text, double? expected) => Assert.Equal(expected, LocalTagValues.GainDb(text));

    [Theory]
    [InlineData("-1280", 0.0)]
    [InlineData("0", 5.0)]
    [InlineData("256", 6.0)]
    [InlineData("x", null)]
    public void R128_IsMovedToTheReplayGainReference(string text, double? expected) =>
        Assert.Equal(expected, LocalTagValues.R128AsReplayGainDb(text));

    [Theory]
    [InlineData("  Title ", "Title")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Text(string? text, string? expected) => Assert.Equal(expected, LocalTagValues.Text(text));
}
