using System.Globalization;
using Avalonia.Threading;
using Rambutan.App.Converters;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Downloads;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;
using Rambutan.Core.Settings;
using Rambutan.LocalLibrary;

namespace Rambutan.App.Tests;

public sealed class DownloadsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static TrackInfo Track(string id) => new()
    {
        VideoId = id,
        Title = $"Song {id}",
        Artists = [new("Artist", "UC1")],
        Duration = TimeSpan.FromMinutes(3),
    };

    private DownloadStore NewStore(GatedFetcher fetcher) =>
        new(_root, () => Path.Combine(_root, "downloads"), fetcher);

    private static TrackActionsViewModel Actions(DownloadQueue? downloads) =>
        new(null!, new QueueModel(), null!, null!, new ToastHostViewModel()) { Downloads = downloads };

    private static string[] LibraryGroup(TrackInfo track, TrackActionsViewModel actions) =>
        TrackMenuModel.Build(track, actions)[1].Select(e => e.Header).ToArray();

    [Fact]
    public async Task List_rows_are_marked_once_their_download_finishes_and_unmarked_when_it_goes()
    {
        var fetcher = new GatedFetcher();
        using var store = NewStore(fetcher);
        var queue = new DownloadQueue(store);
        var actions = Actions(queue);
        var track = Track("vid00000001");

        // What a row's MultiBinding does: its VideoId against the actions' set.
        bool Marked()
        {
            Headless.Run(() => Dispatcher.UIThread.RunJobs()); // the set is replaced on the UI thread
            return (bool)IsDownloadedConverter.Instance.Convert(
                [track.VideoId, actions.DownloadedIds], typeof(bool), null, CultureInfo.InvariantCulture);
        }

        Assert.False(Marked());

        actions.DownloadCommand.Execute(track);
        Assert.False(Marked()); // queued isn't downloaded
        fetcher.Release();
        await WaitUntilAsync(Marked);

        actions.RemoveDownloadCommand.Execute(track);
        await WaitUntilAsync(() => !Marked());
    }

    [Fact]
    public async Task A_store_with_downloads_marks_them_from_the_start()
    {
        var fetcher = new GatedFetcher();
        using (var first = NewStore(fetcher))
        {
            var queue = new DownloadQueue(first);
            queue.Enqueue([Track("vid00000002")]);
            fetcher.Release();
            await WaitUntilAsync(() => queue.IsDownloaded("vid00000002"));
        }

        using var store = NewStore(new GatedFetcher());
        var actions = Actions(new DownloadQueue(store));

        Assert.Equal(["vid00000002"], actions.DownloadedIds);
    }

    [Fact]
    public async Task The_track_menu_offers_download_then_cancel_then_remove()
    {
        var fetcher = new GatedFetcher();
        using var store = NewStore(fetcher);
        var queue = new DownloadQueue(store);
        var actions = Actions(queue);
        var track = Track("vid00000001");

        Assert.Equal(["Like", "Save to playlist", "Download"], LibraryGroup(track, actions));

        actions.DownloadCommand.Execute(track);
        Assert.Equal(["Like", "Save to playlist", "Cancel download"], LibraryGroup(track, actions));

        fetcher.Release();
        await WaitUntilAsync(() => queue.IsDownloaded(track.VideoId));
        Assert.Equal(["Like", "Save to playlist", "Remove download"], LibraryGroup(track, actions));

        actions.RemoveDownloadCommand.Execute(track);
        Assert.False(queue.IsDownloaded(track.VideoId));
    }

    [Fact]
    public void Without_downloads_the_menu_has_no_download_entry() =>
        Assert.Equal(["Like", "Save to playlist"], LibraryGroup(Track("a"), Actions(null)));

    [Fact]
    public async Task The_page_follows_a_job_from_waiting_to_downloaded()
    {
        var fetcher = new GatedFetcher();
        using var store = NewStore(fetcher);
        var queue = new DownloadQueue(store, maxParallel: 1);
        var page = new DownloadsPageViewModel(queue, null, null, post: a => { lock (_root) a(); });

        Assert.True(page.IsEmpty);
        queue.Enqueue([Track("vid00000001"), Track("vid00000002")]);
        await WaitUntilAsync(() => page.Jobs.Count == 2 && page.Jobs[0].Status.Contains('%'));

        Assert.False(page.IsEmpty);
        Assert.Equal("Waiting", page.Jobs[1].Status);
        Assert.Equal("2 songs left", page.JobsSummary);

        fetcher.Release();
        fetcher.Release();
        await WaitUntilAsync(() => page.Downloaded.Count == 2 && page.Jobs.Count == 0);
        Assert.False(page.HasJobs);
        Assert.Equal("2 songs • 1 MB", page.Summary);
    }

    [Fact]
    public async Task A_failed_job_shows_its_state_and_retries()
    {
        var fetcher = new GatedFetcher { FailNext = true };
        using var store = NewStore(fetcher);
        var queue = new DownloadQueue(store);
        var toasts = new ToastHostViewModel();
        var actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, toasts) { Downloads = queue };
        var page = new DownloadsPageViewModel(queue, null, actions, post: a => { lock (_root) a(); });

        queue.Enqueue([Track("vid00000001")]);
        fetcher.Release();
        await WaitUntilAsync(() => page.HasFailed);

        Assert.Equal("Couldn't download", page.Jobs.Single().Status);
        Assert.Contains(toasts.Toasts, t => t.Message == "Couldn't download Song vid00000001. Retry it from Downloads.");

        page.RetryAllCommand.Execute(null);
        fetcher.Release();
        await WaitUntilAsync(() => page.Downloaded.Count == 1);
    }

    [Fact]
    public async Task A_missing_download_is_marked_and_downloads_again()
    {
        var fetcher = new GatedFetcher();
        using var store = NewStore(fetcher);
        var queue = new DownloadQueue(store);
        fetcher.Release();
        queue.Enqueue([Track("vid00000001")]);
        await WaitUntilAsync(() => queue.IsDownloaded("vid00000001"));
        File.Delete(store.TryGetMedia("vid00000001")!.Url);

        var page = new DownloadsPageViewModel(queue, null, null, post: a => { lock (_root) a(); });
        var row = page.Downloaded.Single();
        Assert.True(row.IsMissing);
        Assert.Equal("Song vid00000001, Artist, file missing", row.AccessibleName);

        fetcher.Release();
        page.DownloadAgainCommand.Execute(row);
        await WaitUntilAsync(() => page.Downloaded.SingleOrDefault() is { IsMissing: false });
    }

    [Fact]
    public void The_folder_setting_accepts_a_writable_folder_and_resets_to_the_default()
    {
        using var store = NewStore(new GatedFetcher());
        var app = new AppSettings();
        var persisted = 0;
        var defaultFolder = Path.Combine(_root, "default");
        var settings = new DownloadsSettingsViewModel(app, store, defaultFolder, () => persisted++);

        Assert.True(settings.IsDefaultFolder);
        Assert.Equal(defaultFolder, settings.Folder);

        var chosen = Path.Combine(_root, "chosen");
        settings.SetFolder(chosen);
        Assert.Equal(chosen, app.DownloadsFolder);
        Assert.Equal(chosen, settings.Folder);
        Assert.False(settings.IsDefaultFolder);
        Assert.Null(settings.FolderError);

        settings.UseDefaultFolderCommand.Execute(null);
        Assert.Null(app.DownloadsFolder);
        Assert.Equal(2, persisted);
    }

    [Fact]
    public void The_folder_setting_refuses_a_folder_it_cannot_write()
    {
        using var store = NewStore(new GatedFetcher());
        var app = new AppSettings();
        var settings = new DownloadsSettingsViewModel(app, store, Path.Combine(_root, "default"), () => { });
        var file = Path.Combine(_root, "a-file");
        Directory.CreateDirectory(_root);
        File.WriteAllText(file, "");

        settings.SetFolder(file); // a file, not a folder

        Assert.NotNull(settings.FolderError);
        Assert.Null(app.DownloadsFolder);
    }

    private sealed class RecordingWriter : IExportWriter
    {
        public List<ExportJob> Jobs { get; } = [];

        public Task WriteAsync(ExportJob job, CancellationToken ct)
        {
            Jobs.Add(job);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Export_shows_only_once_the_setting_is_on_and_writes_the_downloads_to_the_picked_folder()
    {
        var fetcher = new GatedFetcher();
        using var store = NewStore(fetcher);
        var queue = new DownloadQueue(store);
        fetcher.Release();
        fetcher.Release();
        queue.Enqueue([Track("vid00000001"), Track("vid00000002")]);
        await WaitUntilAsync(() => queue.IsDownloaded("vid00000001") && queue.IsDownloaded("vid00000002"));

        var app = new AppSettings();
        var export = new ExportSettings(app, () => { });
        var writer = new RecordingWriter();
        var toasts = new ToastHostViewModel();
        var actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, toasts) { Downloads = queue };
        var page = new DownloadsPageViewModel(queue, null, actions, post: a => a(), exportSettings: export,
            export: new DownloadExport(writer, _ => false), exportSource: store.ExportSourceFor);
        var folder = Path.Combine(_root, "exported");
        string? offered = "unset";
        page.PickFolder = start =>
        {
            offered = start;
            return Task.FromResult<string?>(folder);
        };

        // Off by default: no export controls, and the command does nothing.
        Assert.False(page.ExportEnabled);
        await page.ExportAllCommand.ExecuteAsync(null);
        Assert.Empty(writer.Jobs);

        var settings = new DownloadsSettingsViewModel(app, store, Path.Combine(_root, "default"), () => { }, export);
        Assert.True(settings.CanExport);
        settings.ExportEnabled = true;
        Assert.True(page.ExportEnabled);
        Assert.True(page.CanExport);

        await page.ExportAllCommand.ExecuteAsync(null);

        Assert.Null(offered); // the first export starts wherever the picker likes
        Assert.Equal(
            [Path.Combine(folder, "Artist - Song vid00000001.ogg"), Path.Combine(folder, "Artist - Song vid00000002.ogg")],
            writer.Jobs.Select(j => j.OutputPath).Order());
        Assert.All(writer.Jobs, j => Assert.False(j.Target.Transcodes));
        Assert.Equal(folder, app.ExportFolder);
        Assert.Contains(toasts.Toasts, t => t.Message == "Exported 2 songs to exported");
        Assert.False(page.IsExporting);

        settings.ExportFormatIndex = 1;
        Assert.Equal("mp3", app.ExportFormat);
        writer.Jobs.Clear();
        await page.ExportOneCommand.ExecuteAsync(page.Downloaded[0]);
        Assert.Equal(folder, offered); // then where the last one went
        Assert.True(writer.Jobs.Single().Target.Transcodes);
        Assert.EndsWith(".mp3", writer.Jobs.Single().OutputPath);
    }

    [Fact]
    public async Task Export_reports_a_download_whose_file_is_gone()
    {
        var fetcher = new GatedFetcher();
        using var store = NewStore(fetcher);
        var queue = new DownloadQueue(store);
        fetcher.Release();
        queue.Enqueue([Track("vid00000001")]);
        await WaitUntilAsync(() => queue.IsDownloaded("vid00000001"));
        var source = store.ExportSourceFor("vid00000001")!;
        File.Delete(source.AudioPath!);

        Assert.Null(store.ExportSourceFor("vid00000001")!.AudioPath);
        Assert.Null(store.ExportSourceFor("nope"));
    }

    [Theory]
    [InlineData(0, "0 MB")]
    [InlineData(1, "1 MB")]
    [InlineData(84L << 20, "84 MB")]
    [InlineData(1229L << 20, "1.2 GB")]
    public void Sizes_read_in_megabytes_then_gigabytes(long bytes, string expected) =>
        Assert.Equal(expected, DownloadsPageViewModel.FormatSize(bytes), StringComparer.Ordinal);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not met");
            await Task.Delay(5);
        }
    }

    /// <summary>Writes half a megabyte per track, each fetch waiting for a
    /// <see cref="Release"/>.</summary>
    private sealed class GatedFetcher : IAudioFetcher
    {
        private readonly SemaphoreSlim _gate = new(0);

        public bool FailNext { get; set; }

        public void Release() => _gate.Release();

        public async Task<FetchedAudio> FetchAsync(
            TrackInfo track, Stream target, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            progress.Report(new DownloadProgress(0, 512 * 1024));
            await _gate.WaitAsync(ct);
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("403");
            }
            await target.WriteAsync(new byte[512 * 1024], ct);
            return new FetchedAudio("opus", "251", AudioQuality.High, null);
        }
    }
}
