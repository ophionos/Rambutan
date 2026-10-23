using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>Local files matched to YouTube Music songs: off by default, what a
/// match fills, its cover, the saved matches, and the track menu.</summary>
public sealed class YouTubeMatchesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-matches-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, LocalFile> _files = [];
    private readonly List<string> _searches = [];
    private readonly List<string> _fetched = [];
    private TaskCompletionSource? _gate;

    public YouTubeMatchesTests()
    {
        Directory.CreateDirectory(_dir);
        Add("local:1", "Disco 2000", "Pulp");
        Add("local:2", "Babies", "Pulp", cover: Path.Combine(_dir, "own.jpg"));
        Add("local:3", title: null, artist: "Pulp");
    }

    // A match saves, and keeps its cover, after the answer is announced, so a
    // write can still be running when a test ends; Windows refuses to delete
    // a folder with a file open in it.
    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(_dir))
                    Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(20);
            }
        }
    }

    private void Add(string id, string? title, string? artist, string? cover = null) =>
        _files[id] = new LocalFile(id, Path.Combine(_dir, id[6..] + ".mp3"), 1, DateTimeOffset.UnixEpoch,
            new LocalFileTags("mp3", TimeSpan.FromSeconds(200), Title: title, Artist: artist), cover);

    private static TrackInfo Song(string title, string id) => new()
    {
        VideoId = id, Title = title, Artists = [new ArtistRef("Pulp", "UCpulp")], Album = "Different Class",
        AlbumBrowseId = "MPREdc", Duration = TimeSpan.FromSeconds(201), ThumbnailUrl = $"https://lh3.googleusercontent.com/{id}=w60-h60",
    };

    private static TrackInfo Local(string id, string title) =>
        new() { VideoId = id, Title = title, Artists = [] };

    private YouTubeMatches Matches(bool enabled = true) => new(
        new LocalMatcher(async (query, _) =>
        {
            lock (_searches) _searches.Add(query);
            if (_gate is { } gate)
                await gate.Task;
            return query.Contains("Disco") ? [Song("Disco 2000", "disco")] : [];
        }),
        id => _files.GetValueOrDefault(id), _dir,
        (url, _) =>
        {
            lock (_fetched) _fetched.Add(url);
            return Task.FromResult(new byte[] { 0xFF, 0xD8, 0xFF });
        })
    {
        Enabled = enabled,
    };

    private static async Task<T> Eventually<T>(Func<T?> read) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (read() is null && DateTime.UtcNow < until)
            await Task.Delay(10);
        return read() ?? throw new TimeoutException();
    }

    [Fact]
    public async Task Off_nothing_is_searched_and_no_kept_match_is_used()
    {
        var matches = Matches();
        Assert.Equal("disco", await matches.RadioSeedAsync(Local("local:1", "Disco 2000"), default));

        matches.Enabled = false;

        Assert.Null(matches.StatusOf(Local("local:1", "Disco 2000")));
        Assert.Null(await matches.RadioSeedAsync(Local("local:1", "Disco 2000"), default));
        Assert.Equal([], matches.Fill(Local("local:1", "Disco 2000")).Artists);
        Assert.Null(matches.CoverPathFor("local:1"));
        Assert.Single(_searches);
    }

    [Fact]
    public async Task A_match_fills_the_names_and_keeps_a_cover_for_a_file_without_one()
    {
        var matches = Matches();
        var changed = 0;
        matches.Changed += () => Interlocked.Increment(ref changed);

        matches.Request(Local("local:1", "Disco 2000"));
        var cover = await Eventually(() => matches.CoverPathFor("local:1"));

        Assert.Equal(Path.Combine(_dir, "local-match-covers", "disco.jpg"), cover);
        Assert.DoesNotContain("w60", Assert.Single(_fetched));
        var filled = matches.Fill(Local("local:1", "Disco 2000"));
        Assert.Equal("Pulp", Assert.Single(filled.Artists).Name);
        Assert.Equal("Different Class", filled.Album);
        Assert.True(changed > 0);
    }

    [Fact]
    public async Task A_file_with_its_own_cover_keeps_it_and_one_without_a_title_is_never_searched()
    {
        _files["local:2"] = _files["local:2"] with { Tags = _files["local:2"].Tags! with { Title = "Disco 2000" } };
        var matches = Matches();

        Assert.Equal("disco", await matches.RadioSeedAsync(Local("local:2", "Disco 2000"), default));
        Assert.Null(matches.CoverPathFor("local:2"));
        Assert.Empty(_fetched);

        Assert.Equal(LocalMatchState.NoTitle, matches.StatusOf(Local("local:3", "3"))!.Value.State);
        Assert.Null(await matches.RadioSeedAsync(Local("local:3", "3"), default));
        Assert.Single(_searches);
    }

    [Fact]
    public async Task Matches_are_saved_in_the_profile_folder_and_read_back()
    {
        var first = Matches();
        await first.RadioSeedAsync(Local("local:1", "Disco 2000"), default);
        await Eventually(() => File.Exists(Path.Combine(_dir, "local-matches.json")) ? "" : null);
        // The save follows the answer; give it the moment it takes.
        await Eventually(() => File.ReadAllText(Path.Combine(_dir, "local-matches.json")).Contains("disco") ? "" : null);

        var second = Matches();
        var status = second.StatusOf(Local("local:1", "Disco 2000"))!.Value;

        Assert.Equal(LocalMatchState.Found, status.State);
        Assert.Equal("disco", status.Song?.VideoId);
        Assert.Single(_searches);
    }

    private string CoverFolder => Path.Combine(_dir, "local-match-covers");

    private async Task<YouTubeMatches> MatchedWithCover()
    {
        var matches = Matches();
        matches.Request(Local("local:1", "Disco 2000"));
        await Eventually(() => matches.CoverPathFor("local:1"));
        return matches;
    }

    [Fact]
    public async Task Covers_no_match_uses_and_leftover_writes_are_removed()
    {
        var matches = await MatchedWithCover();
        File.WriteAllBytes(Path.Combine(CoverFolder, "expired.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(CoverFolder, "disco.jpg.tmp"), [1]);

        await matches.RemoveUnusedCoversAsync();

        Assert.Equal(["disco.jpg"], Directory.GetFiles(CoverFolder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_file_that_left_the_index_or_was_retagged_or_got_its_own_cover_lets_its_match_cover_go()
    {
        foreach (var change in new Action[]
                 {
                     () => _files.Remove("local:1"),
                     () => _files["local:1"] = _files["local:1"] with { Tags = _files["local:1"].Tags! with { Title = "Mis-Shapes" } },
                     () => _files["local:1"] = _files["local:1"] with { CoverPath = Path.Combine(_dir, "own.jpg") },
                 })
        {
            Add("local:1", "Disco 2000", "Pulp");
            // Else the next round reads the saved match and never fetches the cover.
            File.Delete(Path.Combine(_dir, "local-matches.json"));
            var matches = await MatchedWithCover();

            change();
            await matches.RemoveUnusedCoversAsync();

            Assert.Empty(Directory.GetFiles(CoverFolder));
        }
    }

    [Fact]
    public async Task A_missing_file_keeps_its_match_cover()
    {
        var matches = await MatchedWithCover();

        _files["local:1"] = _files["local:1"] with { IsMissing = true };
        await matches.RemoveUnusedCoversAsync();

        Assert.True(File.Exists(Path.Combine(CoverFolder, "disco.jpg")));
    }

    [Fact]
    public async Task Turning_matching_off_deletes_the_kept_covers()
    {
        var matches = await MatchedWithCover();

        matches.Enabled = false;

        await Eventually(() => Directory.Exists(CoverFolder) ? null : "");
        // At startup with the setting off, a folder left from before goes too.
        Directory.CreateDirectory(CoverFolder);
        File.WriteAllBytes(Path.Combine(CoverFolder, "disco.jpg"), [1]);
        await Matches(enabled: false).RemoveUnusedCoversAsync();
        Assert.False(Directory.Exists(CoverFolder));
    }

    [Fact]
    public async Task A_cover_fetched_after_matching_was_turned_off_is_not_kept()
    {
        var fetching = new TaskCompletionSource();
        var release = new TaskCompletionSource<byte[]>();
        var matches = new YouTubeMatches(
            new LocalMatcher((_, _) => Task.FromResult<IReadOnlyList<TrackInfo>>([Song("Disco 2000", "disco")])),
            id => _files.GetValueOrDefault(id), _dir,
            (_, _) =>
            {
                fetching.TrySetResult();
                return release.Task;
            })
        {
            Enabled = true,
        };
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        matches.Changed += () => announced.TrySetResult();

        matches.Request(Local("local:1", "Disco 2000"));
        await fetching.Task.WaitAsync(TimeSpan.FromSeconds(10));
        matches.Enabled = false;
        announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release.SetResult([0xFF, 0xD8, 0xFF]);
        await announced.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(File.Exists(Path.Combine(CoverFolder, "disco.jpg")));
    }

    private static readonly TrackActionsViewModel NoMatches = new(null!, new QueueModel(), null!, null!, new ToastHostViewModel());

    private static string[][] Headers(IReadOnlyList<IReadOnlyList<MenuEntry>> groups) =>
        groups.Select(g => g.Select(e => e.Header).ToArray()).ToArray();

    [Fact]
    public async Task The_menu_says_it_is_looking_then_names_the_song_and_acts_on_it()
    {
        _gate = new TaskCompletionSource();
        var matches = Matches();
        var actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, new ToastHostViewModel())
        {
            YouTubeMatches = matches,
        };
        var local = Local("local:1", "Disco 2000");

        var looking = TrackMenuModel.Build(local, actions);
        Assert.Equal(
            [
                ["Play next", "Add to queue"],
                ["Looking on YouTube Music…", "Start a mix", "Like", "Save to playlist"],
                ["Go to album", "Go to artist", "Share"],
            ],
            Headers(looking));
        Assert.All(looking[1], e => Assert.False(e.IsEnabled));

        _gate.SetResult();
        await Eventually(() => matches.StatusOf(local) is { State: LocalMatchState.Found } ? "" : null);
        var found = TrackMenuModel.Build(local, actions)[1];

        Assert.Equal("On YouTube Music: Disco 2000 · Pulp", found[0].Header);
        Assert.False(found[0].IsEnabled);
        Assert.All(found.Skip(1), e =>
        {
            Assert.True(e.IsEnabled);
            Assert.Equal("disco", Assert.IsType<TrackInfo>(e.Parameter).VideoId);
            Assert.True(e.Command.CanExecute(e.Parameter));
        });
        Assert.Single(_searches);
    }

    [Fact]
    public async Task The_menu_says_when_nothing_was_found_and_leaves_the_actions_off()
    {
        Add("local:4", "Something Else", "Pulp");
        var matches = Matches();
        var actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, new ToastHostViewModel())
        {
            YouTubeMatches = matches,
        };
        await matches.RadioSeedAsync(Local("local:4", "Something Else"), default);

        var group = TrackMenuModel.Build(Local("local:4", "Something Else"), actions)[1];

        Assert.Equal("Not found on YouTube Music", group[0].Header);
        Assert.All(group, e => Assert.False(e.IsEnabled));
    }

    [Fact]
    public async Task A_failed_search_says_so_and_the_menu_filling_again_does_not_search_again()
    {
        var searches = 0;
        var matches = new YouTubeMatches(
            new LocalMatcher((_, _) =>
            {
                Interlocked.Increment(ref searches);
                return Task.FromException<IReadOnlyList<TrackInfo>>(new HttpRequestException("offline"));
            }),
            id => _files.GetValueOrDefault(id), _dir, (_, _) => Task.FromResult(Array.Empty<byte>()))
        {
            Enabled = true,
        };
        var actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, new ToastHostViewModel())
        {
            YouTubeMatches = matches,
        };
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        matches.Changed += () => announced.TrySetResult();
        var local = Local("local:1", "Disco 2000");

        Assert.Equal(LocalMatchState.Searching, matches.StatusOf(local)!.Value.State);
        await announced.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // What the open menu does when told: it builds itself again.
        var group = TrackMenuModel.Build(local, actions)[1];

        Assert.Equal("Couldn't reach YouTube Music", group[0].Header);
        Assert.All(group, e => Assert.False(e.IsEnabled));
        Assert.Equal(LocalMatchState.Failed, matches.StatusOf(local)!.Value.State);
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref searches));
    }

    [Fact]
    public void Without_matching_a_local_file_menu_has_no_YouTube_Music_group()
    {
        var actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, new ToastHostViewModel())
        {
            YouTubeMatches = Matches(enabled: false),
        };

        Assert.Equal(Headers(TrackMenuModel.Build(Local("local:1", "Disco 2000"), NoMatches)),
            Headers(TrackMenuModel.Build(Local("local:1", "Disco 2000"), actions)));
        Assert.Empty(_searches);
    }
}
