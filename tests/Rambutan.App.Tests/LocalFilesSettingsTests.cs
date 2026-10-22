using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Local;
using Rambutan.LocalLibrary;

namespace Rambutan.App.Tests;

/// <summary>Settings → Music on this computer: adding, removing and rescanning
/// watched folders, and what the status line says.</summary>
public sealed class LocalFilesSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-localset-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _app = new();
    private readonly LocalLibraryScans _scans;
    private readonly LocalFilesSettingsViewModel _vm;
    private int _persisted;

    public LocalFilesSettingsTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "music"));
        _scans = new LocalLibraryScans(new LocalFileIndex(
            Path.Combine(_dir, "data"), new TitleReader(), _ => Task.CompletedTask,
            () => _app.WatchedFolders ?? []));
        // Refreshed by hand: a scan thread's refresh would race the test's.
        _vm = new LocalFilesSettingsViewModel(_app, _scans, () => _persisted++, post: _ => { });
    }

    public void Dispose()
    {
        _scans.Index.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Music => Path.Combine(_dir, "music");

    private async Task WaitForScanAsync()
    {
        for (var i = 0; i < 200 && (_scans.Index.Status.IsScanning || _scans.Index.Files().Count == 0); i++)
            await Task.Delay(10);
        await Task.Delay(20);
        _vm.Refresh();
    }

    [Fact]
    public void MatchingOnYouTubeMusic_IsOffByDefault_AndTurningItOnIsSaved_AndToldToTheMatches()
    {
        var matches = new YouTubeMatches(
            new LocalMatcher((_, _) => Task.FromResult<IReadOnlyList<Rambutan.Core.Models.TrackInfo>>([])),
            _ => null, _dir, (_, _) => Task.FromResult(Array.Empty<byte>()));
        var vm = new LocalFilesSettingsViewModel(_app, _scans, () => _persisted++, post: _ => { }, matches: matches);
        Assert.False(vm.MatchOnYouTubeMusic);
        Assert.False(matches.Enabled);

        vm.MatchOnYouTubeMusic = true;

        Assert.True(_app.MatchLocalOnYouTubeMusic);
        Assert.True(matches.Enabled);
        Assert.Equal(1, _persisted);
    }

    [Fact]
    public void WithNoFolders_ThereIsNothingToShow()
    {
        Assert.False(_vm.HasFolders);
        Assert.Empty(_vm.Folders);
    }

    [Fact]
    public async Task AddingAFolder_SavesIt_AndScansIt()
    {
        File.WriteAllText(Path.Combine(Music, "a.mp3"), "A");
        File.WriteAllText(Path.Combine(Music, "b.mp3"), "bad");

        _vm.AddFolder(Music + Path.DirectorySeparatorChar);
        await WaitForScanAsync();

        Assert.Equal([Music], _app.WatchedFolders);
        Assert.Equal(1, _persisted);
        Assert.True(_vm.HasFolders);
        Assert.Equal(Music, Assert.Single(_vm.Folders).Path);
        Assert.Equal("1 music file", _vm.Status);
        Assert.Equal("1 file couldn't be read as music.", _vm.Problems);
    }

    [Fact]
    public void AFolderAlreadyWatched_IsNotAddedTwice()
    {
        _vm.AddFolder(Music);
        _vm.AddFolder(Music);

        Assert.Single(_app.WatchedFolders!);
        Assert.Equal(1, _persisted);
    }

    [Fact]
    public void AddingAFolder_ReplacesTheList_RatherThanChangingIt()
    {
        _vm.AddFolder(Music);
        var before = _app.WatchedFolders;

        _vm.AddFolder(_dir);

        Assert.NotSame(before, _app.WatchedFolders);
        Assert.Single(before!);
    }

    [Fact]
    public async Task RemovingAFolder_StopsListingItsFiles()
    {
        File.WriteAllText(Path.Combine(Music, "a.mp3"), "A");
        _vm.AddFolder(Music);
        await WaitForScanAsync();

        _vm.RemoveFolderCommand.Execute(_vm.Folders[0]);

        Assert.Empty(_app.WatchedFolders!);
        Assert.False(_vm.HasFolders);
        Assert.Empty(_scans.Index.Files());
        Assert.Equal(2, _persisted);
    }

    [Fact]
    public async Task AFolderThatIsNotThere_IsMarked()
    {
        var gone = Path.Combine(_dir, "usb");
        _vm.AddFolder(gone);
        for (var i = 0; i < 200 && _scans.Index.Status.UnavailableFolders.Count == 0; i++)
            await Task.Delay(10);

        _vm.Refresh();

        Assert.True(Assert.Single(_vm.Folders).IsUnavailable);
    }

    [Fact]
    public void ARemoveButton_IsNamedForItsFolder() =>
        Assert.Equal($"Remove {Music}", new WatchedFolderRow(Music, false).RemoveName);

    private sealed class TitleReader : IMediaFileReader
    {
        public LocalFileTags? Read(string path) =>
            File.ReadAllText(path) is "bad" ? null : new LocalFileTags("mp3", Title: File.ReadAllText(path));
    }
}
