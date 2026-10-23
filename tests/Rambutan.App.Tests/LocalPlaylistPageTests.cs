using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.Core.Playlists;
using Rambutan.Core.Queue;
using Rambutan.LocalLibrary;

namespace Rambutan.App.Tests;

/// <summary>A playlist on this computer: its rows as they show now, files found
/// again where they moved, and its edits, which work as on the account's own
/// YouTube Music playlist.</summary>
public sealed class LocalPlaylistPageTests : IDisposable
{
    private sealed class MemoryStore(LoadedLocalPlaylists? loaded = null) : ILocalPlaylistStore
    {
        public bool Fail { get; set; }
        public int Saves { get; private set; }

        public LoadedLocalPlaylists Load() => loaded ?? new([]);

        public void Save(IReadOnlyList<LocalPlaylist> playlists)
        {
            if (Fail)
                throw new IOException("disk full");
            Saves++;
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-localpl-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _folders = [];
    private readonly LocalLibraryScans _scans;
    private readonly FakeCatalog _catalog = new();
    private readonly ToastHostViewModel _toasts = new(new ManualClock().Schedule);
    private readonly QueueModel _queue = new();
    private readonly TrackActionsViewModel _actions;
    private MemoryStore _store = new();
    private LocalPlaylistLibrary _library;

    public LocalPlaylistPageTests()
    {
        Directory.CreateDirectory(Music);
        _scans = new LocalLibraryScans(new LocalFileIndex(
            Path.Combine(_dir, "data"), new TagReader(), _ => Task.CompletedTask, () => _folders));
        _actions = new TrackActionsViewModel(_catalog, null!, null!, new TrackRatings(_catalog), _toasts);
        _library = new LocalPlaylistLibrary(_store);
    }

    public void Dispose()
    {
        _scans.Index.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Music => Path.Combine(_dir, "music");

    private string LastToast => _toasts.Toasts[^1].Message;

    /// <summary>The reader takes "artist|album|track|title" from the file.</summary>
    private async Task<LocalFile> FileAsync(string name, string content)
    {
        File.WriteAllText(Path.Combine(Music, name), content);
        _folders.Clear();
        _folders.Add(Music);
        await _scans.Index.ScanAsync();
        return _scans.Index.Files().Single(f => f.Path.EndsWith(name, StringComparison.Ordinal));
    }

    private static TrackInfo Song(string id) => new() { VideoId = id, Title = id.ToUpperInvariant(), Artists = [] };

    private static LocalPlaylistItem Gone() =>
        new(new TrackInfo { VideoId = "local:99", Title = "Gone", Artists = [new ArtistRef("Nobody", null)] },
            "/nowhere/gone.mp3");

    private async Task<LocalPlaylistPageViewModel> OpenAsync(params LocalPlaylistItem[] items)
    {
        var playlist = _library.Create("Evening walk", items);
        var page = new LocalPlaylistPageViewModel(
            _library, playlist.Id, _scans, FakePlayback.Coordinator(_queue), null, _actions, post: _ => { });
        await page.ShowAsync();
        return page;
    }

    private static string[] Titles(LocalPlaylistPageViewModel page) => page.Rows.Select(r => r.Title).ToArray();

    [Fact]
    public async Task SongsAndFiles_ShowTogether_AndAMissingFileSaysSoInItsName()
    {
        var file = await FileAsync("a.mp3", "A|Album|1|Only");

        var page = await OpenAsync(LocalPlaylistItem.Of(Song("v")), LocalPlaylistItem.Of(file), Gone());

        Assert.Equal(["V", "Only", "Gone"], Titles(page));
        Assert.Equal([false, false, true], page.Rows.Select(r => r.IsMissing));
        Assert.Equal("Gone, Nobody, file missing", page.Rows[2].AccessibleName);
        Assert.Equal("Evening walk", page.Title);
        Assert.True(page.HasPlayable);
        Assert.False(page.IsEmpty);
        Assert.False(page.IsLoading);
    }

    [Fact]
    public async Task AFileThatMoved_IsFoundByItsTags_AndTheFindIsKept()
    {
        var file = await FileAsync("moved.mp3", "A|Album|1|Only");
        var before = new LocalPlaylistItem(
            new TrackInfo { VideoId = "local:99", Title = "Only", Artists = [new ArtistRef("A", null)], Album = "Album" },
            "/old/place.mp3");

        var page = await OpenAsync(before);

        Assert.False(page.Rows[0].IsMissing);
        Assert.Equal(file.Id, page.Rows[0].Track.VideoId);
        var kept = _library.Find(page.Id)!.Items[0];
        Assert.Equal(file.Id, kept.Track.VideoId);
        Assert.Equal(file.Path, kept.Path);
    }

    [Fact]
    public async Task PlayAll_LeavesOutTheMissingFiles()
    {
        var page = await OpenAsync(LocalPlaylistItem.Of(Song("a")), Gone(), LocalPlaylistItem.Of(Song("b")));

        await page.PlayAllCommand.ExecuteAsync(null);

        Assert.Equal(["a", "b"], _queue.Items.Select(t => t.VideoId));
    }

    [Fact]
    public async Task AMissingRow_SaysWhyItDoesntPlay()
    {
        var page = await OpenAsync(LocalPlaylistItem.Of(Song("a")), Gone());

        await page.PlayFromCommand.ExecuteAsync(page.Rows[1]);

        Assert.Empty(_queue.Items);
        Assert.Equal("“Gone” is no longer on this computer.", LastToast);
    }

    [Fact]
    public async Task ARowPlays_WithThePlayableRowsAsItsQueue()
    {
        var page = await OpenAsync(LocalPlaylistItem.Of(Song("a")), Gone(), LocalPlaylistItem.Of(Song("b")));

        await page.PlayFromCommand.ExecuteAsync(page.Rows[2]);

        Assert.Equal(["a", "b"], _queue.Items.Select(t => t.VideoId));
        Assert.Equal("b", _queue.Current?.VideoId);
    }

    [Fact]
    public async Task ARemovedRow_GoesAtOnce_AndUndoPutsItBackInItsPlace()
    {
        var page = await OpenAsync(LocalPlaylistItem.Of(Song("a")), LocalPlaylistItem.Of(Song("b")), LocalPlaylistItem.Of(Song("c")));

        page.RemoveRowCommand.Execute(page.Rows[1]);

        Assert.Equal(["A", "C"], Titles(page));
        Assert.Equal(["a", "c"], _library.Find(page.Id)!.Items.Select(i => i.Track.VideoId));
        Assert.Equal("Removed “B”", LastToast);

        await _toasts.Toasts[^1].ActCommand.ExecuteAsync(null);
        await page.RefreshAsync();

        Assert.Equal(["a", "b", "c"], _library.Find(page.Id)!.Items.Select(i => i.Track.VideoId));
        Assert.Equal(["A", "B", "C"], Titles(page));
    }

    [Fact]
    public async Task ASongThereTwice_PlaysAndIsRemovedAsTheRowActedOn()
    {
        // One instance for both copies, so the rows' items are equal.
        var a = Song("a");
        var copy = _library.Copy("Copy", [a, Song("b"), a], "PLx");
        var page = new LocalPlaylistPageViewModel(
            _library, copy.Id, _scans, FakePlayback.Coordinator(_queue), null, _actions, post: _ => { });
        await page.ShowAsync();

        await page.PlayFromCommand.ExecuteAsync(page.Rows[2]);
        Assert.Equal(2, _queue.CurrentIndex);

        page.RemoveRowCommand.Execute(page.Rows[2]);
        Assert.Equal(["A", "B"], Titles(page));
        await _toasts.Toasts[^1].ActCommand.ExecuteAsync(null);
        await page.RefreshAsync();
        Assert.Equal(["a", "b", "a"], _library.Find(copy.Id)!.Items.Select(i => i.Track.VideoId));
        Assert.Equal(["A", "B", "A"], Titles(page));
    }

    [Fact]
    public async Task ARowThatCantBeRemoved_StaysAndSaysWhy()
    {
        var page = await OpenAsync(LocalPlaylistItem.Of(Song("a")));
        _store.Fail = true;

        page.RemoveRowCommand.Execute(page.Rows[0]);

        Assert.Equal(["A"], Titles(page));
        Assert.Equal("Couldn't remove “A”: disk full", LastToast);
        Assert.True(_toasts.Toasts[^1].IsError);
    }

    [Fact]
    public async Task AMove_IsSaved()
    {
        var page = await OpenAsync(LocalPlaylistItem.Of(Song("a")), LocalPlaylistItem.Of(Song("b")), LocalPlaylistItem.Of(Song("c")));

        page.DragReorderCommand!.Execute(new ReorderRequest(0, 2));

        Assert.Equal(["B", "C", "A"], Titles(page));
        Assert.Equal(["b", "c", "a"], _library.Find(page.Id)!.Items.Select(i => i.Track.VideoId));
    }

    [Fact]
    public async Task AMoveThatCantBeSaved_PutsTheRowBack()
    {
        var page = await OpenAsync(LocalPlaylistItem.Of(Song("a")), LocalPlaylistItem.Of(Song("b")), LocalPlaylistItem.Of(Song("c")));
        _store.Fail = true;

        page.DragReorderCommand!.Execute(new ReorderRequest(0, 2));

        Assert.Equal(["A", "B", "C"], Titles(page));
        Assert.Equal("Couldn't move “A”: disk full", LastToast);
    }

    [Fact]
    public async Task Rename_ChangesTheName()
    {
        var page = await OpenAsync();
        _actions.TextPrompt = (_, _) => Task.FromResult<string?>("  Night walk ");

        await page.RenameCommand.ExecuteAsync(null);

        Assert.Equal("Night walk", page.Title);
        Assert.Equal("Night walk", _library.Find(page.Id)!.Name);
    }

    [Fact]
    public async Task Delete_AsksFirst()
    {
        var page = await OpenAsync();
        var answer = false;
        _actions.Confirm = _ => Task.FromResult(answer);

        await page.DeleteCommand.ExecuteAsync(null);
        Assert.NotNull(_library.Find(page.Id));

        answer = true;
        await page.DeleteCommand.ExecuteAsync(null);
        Assert.Null(_library.Find(page.Id));
        Assert.Equal("Deleted “Evening walk”", LastToast);
    }

    [Fact]
    public async Task AnEmptyPlaylist_SaysHowSongsGetIntoIt()
    {
        var page = await OpenAsync();

        Assert.True(page.IsEmpty);
        Assert.False(page.HasPlayable);
        Assert.Equal("", page.Summary);
    }

    [Fact]
    public async Task ADeletedPlaylist_SaysItIsGone()
    {
        var page = await OpenAsync(LocalPlaylistItem.Of(Song("a")));
        _library.Delete(page.Id);

        await page.RefreshAsync();

        Assert.True(page.IsGone);
        Assert.False(page.IsEmpty);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task ReadOnlyPlaylists_ShowTheirReason_AndOfferNoEdits()
    {
        var playlist = new LocalPlaylist("p1", "Kept", [LocalPlaylistItem.Of(Song("a")), LocalPlaylistItem.Of(Song("b"))],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        _library = new LocalPlaylistLibrary(new MemoryStore(new([playlist], LocalPlaylistsProblem.Newer)));
        var page = new LocalPlaylistPageViewModel(_library, "p1", _scans, null, null, _actions, post: _ => { });

        await page.ShowAsync();

        Assert.Equal(["A", "B"], Titles(page));
        Assert.False(page.IsEditable);
        Assert.StartsWith("Your playlists on this computer were saved by a newer version", page.ReadOnlyNote);
        Assert.Null(page.DragReorderCommand);
        Assert.Null(page.ListHelp);
        Assert.False(page.RemoveRowCommand.CanExecute(page.Rows[0]));
        Assert.False(page.RenameCommand.CanExecute(null));
        Assert.Same(TrackMenuExtras.None, page.MenuExtrasFor(page.Rows[0], page.Rows[0].Track));
    }

    [Fact]
    public async Task TheMenu_OffersToRemoveTheRow()
    {
        var page = await OpenAsync(LocalPlaylistItem.Of(Song("a")));

        var extras = page.MenuExtrasFor(page.Rows[0], page.Rows[0].Track);

        var remove = Assert.Single(extras.Edit!);
        Assert.Equal("Remove from playlist", remove.Header);
        Assert.Same(page.Rows[0], remove.Parameter);
    }

    [Fact]
    public async Task ItFollowsChanges_OnlyWhileShown()
    {
        var playlist = _library.Create("Mix");
        var page = new LocalPlaylistPageViewModel(
            _library, playlist.Id, _scans, null, null, _actions, post: action => action());
        await page.ShowAsync();

        _library.Add(playlist.Id, [LocalPlaylistItem.Of(Song("a"))]);
        await page.RefreshAsync();
        Assert.Equal(["A"], Titles(page));

        page.Hide();
        _library.Add(playlist.Id, [LocalPlaylistItem.Of(Song("b"))]);
        Assert.Equal(["A"], Titles(page));
    }

    private sealed class TagReader : IMediaFileReader
    {
        public LocalFileTags? Read(string path)
        {
            var parts = File.ReadAllText(path).Split('|');
            return new LocalFileTags("mp3", Title: parts[3], Artist: parts[0], Album: parts[1],
                TrackNumber: int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
