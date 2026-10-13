using Rambutan.Core.Downloads;
using Rambutan.Core.Models;

namespace Rambutan.Core.Tests;

public class DownloadExportTests
{
    private const string Folder = "out";

    private static TrackInfo Track(string id, string title = "Hello", params string[] artists) => new()
    {
        VideoId = id,
        Title = title,
        Artists = artists.Length == 0 ? [new ArtistRef("Adele", null)] : artists.Select(a => new ArtistRef(a, null)).ToArray(),
    };

    private static ExportSource Source(TrackInfo track, string codec = "opus", string? audio = "a.webm") =>
        new(track, audio, codec, "a.jpg");

    private sealed class FakeWriter : IExportWriter
    {
        public List<ExportJob> Written { get; } = [];
        public Func<ExportJob, CancellationToken, Task>? OnWrite { get; set; }

        public async Task WriteAsync(ExportJob job, CancellationToken ct)
        {
            if (OnWrite is not null)
                await OnWrite(job, ct);
            Written.Add(job);
        }
    }

    [Theory]
    [InlineData("opus", ExportFormat.Original, ".ogg", false)]
    [InlineData("mp4a.40.2", ExportFormat.Original, ".m4a", false)]
    [InlineData("opus", ExportFormat.Mp3, ".mp3", true)]
    [InlineData("mp4a.40.2", ExportFormat.Mp3, ".mp3", true)]
    [InlineData("", ExportFormat.Original, ".mp3", true)]
    public void Target_RemuxesOriginalCodecs_AndTranscodesOnlyToMp3(string codec, ExportFormat format, string extension, bool transcodes)
    {
        var target = ExportTarget.For(codec, format);

        Assert.Equal(extension, target.Extension);
        Assert.Equal(transcodes, target.Transcodes);
    }

    [Theory]
    [InlineData("Hello", new[] { "Adele" }, "Adele - Hello")]
    [InlineData("Ain't It Fun", new[] { "Paramore" }, "Paramore - Ain't It Fun")]
    [InlineData("Up/Down: A <Story>?", new[] { "A|B" }, "A_B - Up_Down_ A _Story__")]
    [InlineData("Duet", new[] { "One", "Two" }, "One, Two - Duet")]
    [InlineData("Trailing...", new[] { "X" }, "X - Trailing")]
    [InlineData("CON", new string[0], "_CON")]
    public void FileStem_IsArtistDashTitle_SafeOnEveryPlatform(string title, string[] artists, string expected)
    {
        var track = new TrackInfo
        {
            VideoId = "v", Title = title, Artists = artists.Select(a => new ArtistRef(a, null)).ToArray(),
        };

        Assert.Equal(expected, DownloadExport.FileStem(track));
    }

    [Fact]
    public void FileStem_IsCappedWithoutSplittingAnEmoji()
    {
        var title = new string('a', DownloadExport.MaxStemLength - "Adele - ".Length - 1) + "😀tail";

        var stem = DownloadExport.FileStem(Track("v", title));

        Assert.True(stem.Length <= DownloadExport.MaxStemLength);
        Assert.False(char.IsHighSurrogate(stem[^1]));
    }

    [Fact]
    public async Task Run_WritesEachDownload_AndNeverReusesAName()
    {
        var writer = new FakeWriter();
        var existing = new HashSet<string> { Path.Combine(Folder, "Adele - Hello.ogg") };
        var export = new DownloadExport(writer, existing.Contains);

        var result = await export.RunAsync(
            [Source(Track("a")), Source(Track("b")), Source(Track("c"), codec: "mp4a.40.2")],
            Folder, ExportFormat.Original, null, CancellationToken.None);

        Assert.Equal(3, result.Written);
        Assert.Empty(result.Failed);
        Assert.Equal(
            [Path.Combine(Folder, "Adele - Hello (2).ogg"), Path.Combine(Folder, "Adele - Hello (3).ogg"),
             Path.Combine(Folder, "Adele - Hello.m4a")],
            writer.Written.Select(j => j.OutputPath));
    }

    [Fact]
    public async Task Run_ReportsMissingFilesAndWriterFailures_AndCarriesOn()
    {
        var writer = new FakeWriter
        {
            OnWrite = (job, _) => job.Track.VideoId == "b" ? throw new IOException("disk full") : Task.CompletedTask,
        };
        var export = new DownloadExport(writer, _ => false);
        var progress = new List<int>();

        var result = await export.RunAsync(
            [Source(Track("a", "One")), Source(Track("b", "Two")), Source(Track("c", "Three"), audio: null), Source(Track("d", "Four"))],
            Folder, ExportFormat.Original, new SyncProgress(progress), CancellationToken.None);

        Assert.Equal(2, result.Written);
        Assert.Equal(["b", "c"], result.Failed.Select(t => t.VideoId));
        Assert.False(result.Cancelled);
        Assert.Equal([1, 2, 3, 4], progress);
    }

    [Fact]
    public async Task Run_StopsWhenCancelled_AndCountsWhatWasWritten()
    {
        using var cts = new CancellationTokenSource();
        var writer = new FakeWriter
        {
            OnWrite = (job, ct) =>
            {
                if (job.Track.VideoId == "b")
                {
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                }
                return Task.CompletedTask;
            },
        };
        var export = new DownloadExport(writer, _ => false);

        var result = await export.RunAsync(
            [Source(Track("a", "One")), Source(Track("b", "Two")), Source(Track("c", "Three"))],
            Folder, ExportFormat.Mp3, null, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(1, result.Written);
        Assert.Equal(["a"], writer.Written.Select(j => j.Track.VideoId));
    }

    /// <summary><see cref="Progress{T}"/> posts to the thread pool; this records in order.</summary>
    private sealed class SyncProgress(List<int> into) : IProgress<int>
    {
        public void Report(int value) => into.Add(value);
    }
}
