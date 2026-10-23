using System.Collections.Concurrent;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Downloads;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;
using Rambutan.Core.Settings;
using Rambutan.LocalLibrary;

namespace Rambutan.App.Tests;

/// <summary>On this computer with the downloads in it: what each source filter
/// lists, the downloads' own states, and how a missing download plays.</summary>
public sealed class LocalLibraryDownloadsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-localdl-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _folders = [];
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly LocalLibraryScans _scans;
    private readonly DownloadStore _store;
    private readonly DownloadQueue _queue;
    private readonly QueueModel _playQueue = new();
    private DownloadsViewModel _downloads;
    private LocalLibraryViewModel _vm;

    public LocalLibraryDownloadsTests()
    {
        Directory.CreateDirectory(Music);
        var data = Path.Combine(_dir, "data");
        _scans = new LocalLibraryScans(new LocalFileIndex(data, new TagReader(), _ => Task.CompletedTask, () => _folders));
        _store = new DownloadStore(_scans.Index, data, () => Path.Combine(_dir, "downloads"), new InstantFetcher());
        _queue = new DownloadQueue(_store);
        (_downloads, _vm) = Open();
    }

    /// <summary>The view models as a start makes them.</summary>
    private (DownloadsViewModel, LocalLibraryViewModel) Open()
    {
        // Run by Pump on the test's thread, as the UI thread would.
        var downloads = new DownloadsViewModel(_queue, null, post: _posted.Enqueue);
        return (downloads, new LocalLibraryViewModel(_scans, FakePlayback.Coordinator(_playQueue), () => _folders,
            post: _ => { }, downloads: downloads));
    }

    public void Dispose()
    {
        _store.Dispose();
        _scans.Index.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Music => Path.Combine(_dir, "music");

    private void Pump()
    {
        while (_posted.TryDequeue(out var action))
            action();
    }

    /// <summary>The reader takes "artist|album|track|title[|video id]" from the file;
    /// a download's file has no tags.</summary>
    private void Write(string name, string content) => File.WriteAllText(Path.Combine(Music, name), content);

    private static TrackInfo Track(string id, string title, string artist) => new()
    {
        VideoId = id,
        Title = title,
        Artists = [new(artist, null)],
        Album = "X",
    };

    private async Task DownloadAsync(params TrackInfo[] tracks)
    {
        var count = _downloads.Downloaded.Count + tracks.Length;
        _queue.Enqueue(tracks);
        await WaitUntilAsync(() =>
        {
            Pump();
            return _downloads.Downloaded.Count == count && !_downloads.HasJobs;
        });
    }

    private async Task ScanAsync()
    {
        _folders.Clear();
        _folders.Add(Music);
        await _scans.Index.ScanAsync();
    }

    private async Task ShowAsync(LocalSource filter)
    {
        _vm.Filter = filter;
        await _vm.ShowAsync();
    }

    [Fact]
    public async Task All_lists_the_downloads_among_the_files_and_leaves_out_an_exported_copy()
    {
        Write("1.mp3", "A|X|1|Apple");
        Write("2.mp3", "C|X|1|Cherry");
        Write("3.mp3", "B|X|2|Exported copy|vid00000002");
        await ScanAsync();
        await DownloadAsync(Track("vid00000001", "Banana", "B"));
        await DownloadAsync(Track("vid00000002", "Berry", "B"));

        await ShowAsync(LocalSource.All);
        Assert.Equal(["Apple", "Banana", "Berry", "Cherry"], _vm.Tracks.Select(r => r.Title));
        Assert.Equal("4 songs", _vm.Status);

        await ShowAsync(LocalSource.Folders);
        Assert.Equal(["Apple", "Exported copy", "Cherry"], _vm.Tracks.Select(r => r.Title));
        Assert.All(_vm.Tracks, r => Assert.False(r.IsDownload));

        await ShowAsync(LocalSource.Downloads);
        Assert.Equal(["Berry", "Banana"], _vm.Tracks.Select(r => r.Title)); // newest first
        Assert.All(_vm.Tracks, r => Assert.True(r.IsDownload));
        Assert.Equal("2 songs • 1 MB", _vm.Status);
        Assert.Equal("", _vm.Problems);
    }

    [Fact]
    public async Task A_missing_download_is_listed_only_by_the_downloads_filter_and_plays_by_downloading_again()
    {
        await ScanAsync();
        await DownloadAsync(Track("vid00000001", "Gone", "A"), Track("vid00000002", "Kept", "A"));
        File.Delete(_store.TryGetMedia("vid00000001")!.Url);
        (_downloads, _vm) = Open(); // a file gone is seen when the list is read

        await ShowAsync(LocalSource.All);
        Assert.Equal(["Kept"], _vm.Tracks.Select(r => r.Title));
        Assert.Equal("1 download is missing.", _vm.Problems);

        await ShowAsync(LocalSource.Downloads);
        var gone = _vm.Tracks.Single(r => r.IsMissing);
        Assert.Equal("Gone, A · X, file missing", gone.AccessibleName);

        await _vm.PlayAllCommand.ExecuteAsync(null);
        Assert.Equal(["vid00000002"], _playQueue.Items.Select(t => t.VideoId));

        await _vm.PlayCommand.ExecuteAsync(gone);
        await WaitUntilAsync(() =>
        {
            Pump();
            return _downloads.Downloaded.All(d => !d.IsMissing) && !_downloads.HasJobs;
        });
        Assert.Equal(["vid00000002"], _playQueue.Items.Select(t => t.VideoId));
    }

    [Fact]
    public async Task The_list_follows_the_downloads_while_shown()
    {
        await ScanAsync();
        await ShowAsync(LocalSource.Downloads);
        Assert.True(_vm.NoDownloads);

        await DownloadAsync(Track("vid00000001", "New", "A"));

        await WaitUntilAsync(() => _vm.Tracks.Count == 1);
        Assert.False(_vm.NoDownloads);
    }

    [Fact]
    public async Task Without_folders_each_filter_says_what_is_missing()
    {
        await ShowAsync(LocalSource.All);
        Assert.True(_vm.NeedsFolders);
        Assert.False(_vm.IsEmpty);

        await ShowAsync(LocalSource.Downloads);
        Assert.False(_vm.NeedsFolders);
        Assert.True(_vm.NoDownloads);

        await DownloadAsync(Track("vid00000001", "Only", "A"));
        await ShowAsync(LocalSource.All);
        Assert.False(_vm.NeedsFolders);
        Assert.Equal(["Only"], _vm.Tracks.Select(r => r.Title));

        await ShowAsync(LocalSource.Folders);
        Assert.True(_vm.NeedsFolders);
        Assert.Empty(_vm.Tracks);
    }

    [Fact]
    public void Going_to_the_downloads_opens_this_computer_filtered_to_them()
    {
        var actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, new ToastHostViewModel());
        var navigation = new NavigationService(new FakeCatalog(), null!, actions, local: _vm);
        navigation.GoLibrary();
        var library = Assert.IsType<LibraryPageViewModel>(navigation.Current);
        Assert.Equal(LibrarySource.YouTubeMusic, library.Source);

        Assert.NotNull(navigation.GoDownloads);
        navigation.GoDownloads();

        Assert.Same(library, navigation.Current);
        Assert.Equal(LibrarySource.ThisComputer, library.Source);
        Assert.Equal(LocalSource.Downloads, _vm.Filter);
        Assert.Equal(NavSection.Library, navigation.Section);
    }

    [Fact]
    public async Task A_download_opened_as_a_file_plays_as_its_track()
    {
        await DownloadAsync(Track("vid00000001", "Song", "A"));
        var played = new List<IReadOnlyList<TrackInfo>>();
        var opener = new LocalFileOpener(_scans.Index,
            tracks => { played.Add(tracks); return Task.CompletedTask; }, _ => { }, new ToastHostViewModel());

        await opener.OpenAsync([_store.TryGetMedia("vid00000001")!.Url], playNow: true);

        var track = Assert.Single(Assert.Single(played));
        Assert.Equal("vid00000001", track.VideoId);
        Assert.Equal("Song", track.Title);
    }

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

    private sealed class InstantFetcher : IAudioFetcher
    {
        public async Task<FetchedAudio> FetchAsync(
            TrackInfo track, Stream target, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            await target.WriteAsync(new byte[512 * 1024], ct);
            return new FetchedAudio("opus", "251", AudioQuality.High, null);
        }
    }

    private sealed class TagReader : IMediaFileReader
    {
        public LocalFileTags? Read(string path)
        {
            var parts = File.ReadAllText(path).Split('|');
            if (parts.Length < 4)
                return new LocalFileTags("opus");
            return new LocalFileTags("mp3", Title: parts[3], Artist: parts[0], Album: parts[1],
                TrackNumber: int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                YouTubeVideoId: parts.Length > 4 ? parts[4] : null);
        }
    }
}
