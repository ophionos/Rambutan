namespace Rambutan.Streams.Tests;

public class WatchHistoryReporterTests
{
    [Fact]
    public void The_cookie_file_has_each_cookie_on_youtube_com()
    {
        var file = WatchHistoryReporter.CookieFile(
            "SAPISID=abc/def; __Secure-3PSID=x=y; ;bare; =novalue; tab=a\tb",
            DateTimeOffset.FromUnixTimeSeconds(1800000000));

        Assert.Equal(
            "# Netscape HTTP Cookie File\n" +
            ".youtube.com\tTRUE\t/\tTRUE\t1800000000\tSAPISID\tabc/def\n" +
            ".youtube.com\tTRUE\t/\tTRUE\t1800000000\t__Secure-3PSID\tx=y\n",
            file);
    }

    [Fact]
    public void The_run_marks_the_song_watched_without_downloading()
    {
        var args = WatchHistoryReporter.Arguments("khnokW3Mw24", "c.txt");

        Assert.Contains("--mark-watched", args);
        Assert.Contains("--simulate", args);
        Assert.Contains("--ignore-no-formats-error", args);
        Assert.Equal("c.txt", args[args.ToList().IndexOf("--cookies") + 1]);
        Assert.Equal("https://music.youtube.com/watch?v=khnokW3Mw24", args[^1]);
        Assert.DoesNotContain("--no-warnings", args);
    }

    [Fact]
    public async Task An_id_that_isnt_YouTubes_is_refused_before_yt_dlp_runs()
    {
        var reporter = new WatchHistoryReporter(
            new YtDlpManager(Path.Combine(Path.GetTempPath(), "rambutan-no-tools")), Path.GetTempPath());

        await Assert.ThrowsAsync<Rambutan.Core.Models.NotAYouTubeTrackException>(() =>
            reporter.ReportAsync("local:3", "SAPISID=a"));
    }

    [Fact]
    public void The_cookies_yt_dlp_ends_with_come_back_when_they_changed()
    {
        var file =
            "# Netscape HTTP Cookie File\n" +
            "# a comment\n" +
            ".youtube.com\tTRUE\t/\tTRUE\t1800000000\tSAPISID\tabc\n" +
            "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1800000000\tSIDCC\tnew\r\n" +
            ".google.com\tTRUE\t/\tTRUE\t1800000000\tNID\tg\n" +
            "music.youtube.com\tFALSE\t/\tTRUE\t0\tYSC\ty\n" +
            ".youtube.com\tTRUE\t/\tFALSE\t1800000000\tPREF\tf6=40000000&hl=en&tz=UTC\n" +
            ".youtube.com\tTRUE\t/\tTRUE\t1800000000\tSOCS\tCAI\n";

        var read = WatchHistoryReporter.ReadCookieFile(file);
        var changed = WatchHistoryReporter.Changed(
            [("SAPISID", "abc"), ("SIDCC", "old"), ("GONE", "x"), ("PREF", "f6=40000000&hl=de")], read);

        Assert.Equal(
            [("SAPISID", "abc"), ("SIDCC", "new"), ("YSC", "y"), ("PREF", "f6=40000000&hl=en&tz=UTC"), ("SOCS", "CAI")],
            read);
        Assert.Equal([("SIDCC", "new"), ("YSC", "y")], changed);
    }
}
