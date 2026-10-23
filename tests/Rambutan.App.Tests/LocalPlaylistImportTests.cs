using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Local;
using Rambutan.Core.Playlists;
using Rambutan.Core.Queue;
using Rambutan.LocalLibrary;

namespace Rambutan.App.Tests;

/// <summary>Importing an M3U playlist file from the Playlists filter.</summary>
public sealed class LocalPlaylistImportTests : IDisposable
{
    private sealed class MemoryStore(LoadedLocalPlaylists? loaded = null) : ILocalPlaylistStore
    {
        public LoadedLocalPlaylists Load() => loaded ?? new([]);
        public void Save(IReadOnlyList<LocalPlaylist> playlists) { }
    }

    /// <summary>A file's text is its title.</summary>
    private sealed class TitleReader : IMediaFileReader
    {
        public LocalFileTags? Read(string path) => new("mp3", Title: File.ReadAllText(path));
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-import-" + Guid.NewGuid().ToString("N"));
    private readonly LocalLibraryScans _scans;
    private readonly FakeCatalog _catalog = new();
    private readonly ToastHostViewModel _toasts = new(new ManualClock().Schedule);
    private readonly TrackActionsViewModel _actions;
    private LocalPlaylistLibrary _library = new(new MemoryStore());

    public LocalPlaylistImportTests()
    {
        Directory.CreateDirectory(_dir);
        _scans = new LocalLibraryScans(new LocalFileIndex(
            Path.Combine(_dir, "data"), new TitleReader(), _ => Task.CompletedTask, () => []));
        _actions = new TrackActionsViewModel(_catalog, new QueueModel(), null!, new TrackRatings(_catalog), _toasts);
    }

    public void Dispose()
    {
        _scans.Index.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private LibraryPageViewModel Page(PickedFile? picked)
    {
        var coordinator = FakePlayback.Coordinator(new QueueModel());
        var local = new LocalLibraryViewModel(_scans, coordinator: null, () => [], post: _ => { }, playlists: _library);
        return new LibraryPageViewModel(_catalog, new NavigationService(_catalog, coordinator, _actions), _actions,
            coordinator, local, LibrarySource.ThisComputer)
        {
            PickPlaylistFile = () => Task.FromResult(picked),
        };
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task AnImport_MakesAPlaylistNamedAfterTheFile_AndOffersToOpenIt()
    {
        Write("a.mp3", "A");
        var list = Write("Evening walk.m3u8", "a.mp3\nhttps://example.com/x.mp3\nlater.mp3\na.mp3\n");
        var page = Page(new PickedFile(list));

        await page.ImportLocalPlaylistCommand.ExecuteAsync(null);

        var playlist = Assert.Single(_library.All);
        Assert.Equal("Evening walk", playlist.Name);
        Assert.Equal(["A", "later", "A"], playlist.Items.Select(i => i.Track.Title));
        var toast = Assert.Single(_toasts.Toasts);
        Assert.Equal("Imported 3 songs as “Evening walk”. Skipped 1 entry: a web address or a file that can't be played.",
            toast.Message);
        Assert.Equal("Open", toast.ActionLabel);
        Assert.False(page.IsImportingPlaylist);
    }

    [Fact]
    public async Task AFileThatNamesNothing_MakesNoPlaylist()
    {
        var list = Write("Radio.m3u8", "#EXTM3U\nhttps://example.com/stream\n");

        await Page(new PickedFile(list)).ImportLocalPlaylistCommand.ExecuteAsync(null);

        Assert.Empty(_library.All);
        Assert.Equal("Radio names no files to import.", Assert.Single(_toasts.Toasts).Message);
    }

    [Fact]
    public async Task AReadOnlyPlaylistsFile_SaysWhy_BeforeAskingForAFile()
    {
        _library = new LocalPlaylistLibrary(new MemoryStore(new([], LocalPlaylistsProblem.Newer)));
        var page = Page(null);
        page.PickPlaylistFile = () => throw new InvalidOperationException("asked");

        await page.ImportLocalPlaylistCommand.ExecuteAsync(null);

        Assert.Equal(TrackActionsViewModel.ReadOnlyNote(_library), Assert.Single(_toasts.Toasts).Message);
    }

    [Fact]
    public async Task APickedFileWithNoPath_SaysSo_AndAFailedRead_SaysWhy()
    {
        await Page(new PickedFile(null)).ImportLocalPlaylistCommand.ExecuteAsync(null);
        await Page(new PickedFile(Path.Combine(_dir, "gone.m3u8"))).ImportLocalPlaylistCommand.ExecuteAsync(null);

        Assert.Equal("That file isn't on this computer, so the files it names can't be found.", _toasts.Toasts[0].Message);
        Assert.StartsWith("Couldn't import gone.m3u8: ", _toasts.Toasts[1].Message);
        Assert.All(_toasts.Toasts, t => Assert.True(t.IsError));
        Assert.Empty(_library.All);
    }
}
