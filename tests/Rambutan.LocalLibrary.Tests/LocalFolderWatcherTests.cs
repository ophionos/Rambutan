namespace Rambutan.LocalLibrary.Tests;

/// <summary>The watcher against a real folder; the rules are tested in Core's
/// <c>LocalWatchTests</c>. Each test waits out the quiet period.</summary>
public sealed class LocalFolderWatcherTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rambutan-watch-{Guid.NewGuid():N}");
    private readonly string _music;
    private readonly string _data;

    public LocalFolderWatcherTests()
    {
        _music = Path.Combine(_root, "music");
        _data = Path.Combine(_music, "rambutan-data"); // the app's folder inside a watched one
        Directory.CreateDirectory(_data);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ANewSong_MakesItsFolderDue()
    {
        using var watcher = new LocalFolderWatcher([_data]);
        var due = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.FoldersDue += folders => due.TrySetResult(folders);
        watcher.Sync([_music]);

        await File.WriteAllTextAsync(Path.Combine(_music, "song.flac"), "song");

        Assert.Equal([_music], await due.Task.WaitAsync(Patience));
        Assert.Empty(watcher.NotWatched);
    }

    [Fact]
    public async Task TheAppsOwnWrites_AndOtherFiles_DontCount()
    {
        using var watcher = new LocalFolderWatcher([_data]);
        var raised = 0;
        watcher.FoldersDue += _ => Interlocked.Increment(ref raised);
        watcher.Sync([_music]);

        await File.WriteAllTextAsync(Path.Combine(_data, "index.mp3"), "not music");
        await File.WriteAllTextAsync(Path.Combine(_music, "notes.txt"), "notes");
        await Task.Delay(Core.Local.LocalWatch.QuietPeriod + TimeSpan.FromSeconds(1));

        Assert.Equal(0, raised);
    }

    [Fact]
    public void AFolderThatIsntThere_IsNeitherWatchedNorMarked()
    {
        using var watcher = new LocalFolderWatcher([]);

        watcher.Sync([Path.Combine(_root, "usb")]);

        Assert.Empty(watcher.NotWatched);
    }
}
