using Rambutan.Core.Diagnostics;

namespace Rambutan.Core.Tests;

/// <summary>
/// The crash report: no personal data in its text, and a GitHub link that
/// stays short enough to open.
/// </summary>
public sealed class CrashReportTests
{
    private static readonly KeyValuePair<string, string>[] Personal =
    [
        new(@"C:\Users\ana\AppData\Roaming\Rambutan", "<data>"),
        new(@"C:\Users\ana", "~"),
        new("ana", "<user>"),
        new("ANA-DESKTOP", "<machine>"),
    ];

    private static CrashReport Report(string exception) =>
        new(new DateTimeOffset(2026, 10, 10, 12, 30, 0, TimeSpan.Zero), "0.1.0-alpha.0+abc123",
            "Microsoft Windows 10.0.26300", ".NET 10.0.0, X64", exception);

    [Fact]
    public void Scrub_ReplacesTheDataFolderBeforeTheHomeFolderThatHoldsIt()
    {
        var text = CrashReport.Scrub(
            @"Could not find file 'C:\Users\ana\AppData\Roaming\Rambutan\queue.json'. Also C:\Users\ana\Music\x.mp3",
            Personal);

        Assert.Equal(@"Could not find file '<data>\queue.json'. Also ~\Music\x.mp3", text);
    }

    [Fact]
    public void Scrub_MatchesFoldersWithEitherSeparatorAndAnyCase()
    {
        var text = CrashReport.Scrub("at c:/users/ANA/source/Program.cs:line 4", Personal);

        Assert.Equal("at ~/source/Program.cs:line 4", text);
    }

    [Fact]
    public void Scrub_ReplacesNamesAsWholeWordsOnly()
    {
        var text = CrashReport.Scrub("user ana on ANA-DESKTOP; banana and analysis stay", Personal);

        Assert.Equal("user <user> on <machine>; banana and analysis stay", text);
    }

    [Fact]
    public void Scrub_IgnoresValuesTooShortToBeSafe()
    {
        var text = CrashReport.Scrub("a b c", [new("a", "<user>"), new(" ", "<x>")]);

        Assert.Equal("a b c", text);
    }

    [Fact]
    public void Scrub_DropsUrlQueries()
    {
        var text = CrashReport.Scrub(
            "GET https://rr1.googlevideo.com/videoplayback?ip=203.0.113.9&sig=abc failed; see http://x.test/a",
            []);

        Assert.Equal("GET https://rr1.googlevideo.com/videoplayback?… failed; see http://x.test/a", text);
    }

    [Fact]
    public void Create_ScrubsTheException()
    {
        var report = CrashReport.Create(DateTimeOffset.UnixEpoch, "1", "os", "rt",
            @"System.IO.IOException: C:\Users\ana\x", Personal);

        Assert.Equal(@"System.IO.IOException: ~\x", report.Exception);
    }

    [Fact]
    public void IssueTitle_NamesTheExceptionType()
    {
        Assert.Equal("Crash: System.InvalidOperationException",
            Report("System.InvalidOperationException: Sequence contains no elements\n   at X.Y()").IssueTitle);
        Assert.Equal("Crash: unhandled exception", Report("").IssueTitle);
    }

    [Fact]
    public void IssueBody_HasTheDetailsAndTheExceptionInACodeBlock()
    {
        var body = Report("System.Exception: boom\n   at A.B()").IssueBody();

        Assert.Contains("- Version: 0.1.0-alpha.0+abc123\n", body);
        Assert.Contains("- OS: Microsoft Windows 10.0.26300\n", body);
        Assert.Contains("- Runtime: .NET 10.0.0, X64\n", body);
        Assert.Contains("- When: 2026-10-10 12:30:00 UTC\n", body);
        Assert.Contains("```\nSystem.Exception: boom\n   at A.B()\n```\n", body);
    }

    [Fact]
    public void IssueBody_CannotCloseItsOwnCodeBlock()
    {
        var body = Report("System.Exception: ``` injected").IssueBody();

        Assert.Contains("System.Exception: ''' injected", body);
    }

    [Fact]
    public void IssueBody_NeverCutsASurrogatePairInTwo()
    {
        var exception = "System.Exception: can't play 🎵 song";
        // Just after the high surrogate: the cut would leave half of the note.
        var cut = exception.IndexOf("🎵", StringComparison.Ordinal) + 1;
        Assert.True(char.IsHighSurrogate(exception[cut - 1]));

        var body = Report(exception).IssueBody(cut);

        Assert.Contains("can't play \n… (cut", body);
        Assert.DoesNotContain(exception[..cut], body);
        Assert.False(string.IsNullOrEmpty(Uri.EscapeDataString(body)));
    }

    [Fact]
    public void IssueUrl_FillsInTheTitleAndBody()
    {
        var url = Report("System.Exception: boom").IssueUrl("https://github.com/owner/repo/");

        Assert.StartsWith("https://github.com/owner/repo/issues/new?title=Crash%3A%20System.Exception&body=", url.AbsoluteUri);
        Assert.Contains(Uri.EscapeDataString("System.Exception: boom"), url.AbsoluteUri);
    }

    [Fact]
    public void IssueUrl_CutsALongExceptionToFit()
    {
        // Characters that escape to nine each (é is two UTF-8 bytes) as well as plain ones.
        var exception = "System.Exception: long\n" + string.Concat(Enumerable.Repeat("   at Ns.Type.Méthode() in <data>\\f.cs\n", 2000));
        var url = Report(exception).IssueUrl("https://github.com/owner/repo");

        Assert.True(url.AbsoluteUri.Length <= CrashReport.MaxIssueUrlLength, $"{url.AbsoluteUri.Length} characters");
        var body = Uri.UnescapeDataString(url.Query[(url.Query.IndexOf("&body=", StringComparison.Ordinal) + 6)..]);
        Assert.Contains("System.Exception: long", body);
        Assert.Contains("cut to fit the link", body);
    }
}
