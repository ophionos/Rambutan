using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Diagnostics;

namespace Rambutan.App.Tests;

/// <summary>
/// The local crash file and the notice on the next start: the file is
/// versioned and never a crash of its own, and the notice sends nothing; it goes
/// once the user reports the crash or dismisses it.
/// </summary>
public sealed class CrashNoticeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-crash-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _opened = [];
    private bool _browserOpens = true;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static CrashReport Report() =>
        new(new DateTimeOffset(2026, 10, 10, 12, 30, 0, TimeSpan.Zero), "0.1.0", "os", "rt",
            "System.Exception: boom\n   at A.B()");

    private ToastHostViewModel Toasts { get; } = new((_, _) => new NoTimer());

    private CrashNoticeViewModel Notice() => new(
        new CrashFile(_dir), "https://github.com/owner/repo",
        url =>
        {
            _opened.Add(url);
            return Task.FromResult(_browserOpens);
        },
        Toasts);

    [Fact]
    public void WrittenCrash_ReadsBack()
    {
        var file = new CrashFile(_dir);
        file.Write(Report());

        Assert.Equal(Report(), file.Read());
        Assert.Contains("\"version\": 1", File.ReadAllText(file.Path));
    }

    [Fact]
    public void Record_KeepsTheExceptionWithoutThisMachinesFolders()
    {
        var file = new CrashFile(_dir);
        file.Record(new InvalidOperationException($"Couldn't open {Path.Combine(_dir, "queue.json")}"));

        var report = file.Read();
        Assert.NotNull(report);
        Assert.StartsWith("System.InvalidOperationException: Couldn't open <data>", report.Exception);
        Assert.DoesNotContain(_dir, report.Exception, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("unknown", report.AppVersion);
    }

    [Fact]
    public void Record_ScrubsTheFoldersTheUserChose()
    {
        var file = new CrashFile(_dir) { UserFolders = () => [@"D:\Music of Ana", null, ""] };
        file.Record(new IOException(@"Couldn't write D:\Music of Ana\song.opus"));

        Assert.Equal(@"System.IO.IOException: Couldn't write <folder>\song.opus", file.Read()!.Exception);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"version": 2, "exception": "System.Exception: from a newer build"}""")]
    [InlineData("""{"version": 1}""")]
    public void UnreadableFile_ReadsAsNoCrash(string content)
    {
        Directory.CreateDirectory(_dir);
        var file = new CrashFile(_dir);
        File.WriteAllText(file.Path, content);

        Assert.Null(file.Read());
    }

    [Fact]
    public void NoCrash_NoNotice()
    {
        var notice = Notice();

        Assert.False(notice.IsVisible);
        Assert.Equal("", notice.Text);
    }

    [Fact]
    public void Crash_ShowsTheNotice_UntilDismissed()
    {
        new CrashFile(_dir).Write(Report());
        var notice = Notice();
        Assert.True(notice.IsVisible);
        Assert.Contains("closed unexpectedly", notice.Text);

        notice.DismissCommand.Execute(null);

        Assert.False(notice.IsVisible);
        Assert.Empty(_opened);
        Assert.Null(new CrashFile(_dir).Read());
        Assert.False(Notice().IsVisible);
    }

    [Fact]
    public async Task Report_OpensTheFilledInIssue_AndClearsTheCrash()
    {
        new CrashFile(_dir).Write(Report());
        var notice = Notice();

        await notice.ReportCommand.ExecuteAsync(null);

        var url = Assert.Single(_opened);
        Assert.StartsWith("https://github.com/owner/repo/issues/new?title=", url);
        Assert.Contains(Uri.EscapeDataString("System.Exception: boom"), url);
        Assert.False(notice.IsVisible);
        Assert.False(File.Exists(new CrashFile(_dir).Path));
    }

    [Fact]
    public async Task Report_WithNoBrowser_KeepsTheNoticeAndSaysSo()
    {
        new CrashFile(_dir).Write(Report());
        _browserOpens = false;
        var notice = Notice();

        await notice.ReportCommand.ExecuteAsync(null);

        Assert.True(notice.IsVisible);
        Assert.NotNull(new CrashFile(_dir).Read());
        Assert.True(Assert.Single(Toasts.Toasts).IsError);
    }

    [Fact]
    public void MainStatusStrip_ShowsWhileTheNoticeDoes()
    {
        new CrashFile(_dir).Write(Report());
        var main = new MainViewModel { CrashNotice = Notice() };
        var changes = new List<string?>();
        main.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.True(main.HasStatus);

        main.CrashNotice!.DismissCommand.Execute(null);

        Assert.False(main.HasStatus);
        Assert.Contains(nameof(MainViewModel.HasStatus), changes);
    }

    private sealed class NoTimer : IDisposable
    {
        public void Dispose() { }
    }
}
