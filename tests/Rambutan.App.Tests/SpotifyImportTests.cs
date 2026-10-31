using System.IO.Compression;
using System.Text;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Import;
using Rambutan.Core.Models;
using Rambutan.Core.Playlists;

namespace Rambutan.App.Tests;

/// <summary>Reading a picked file, and what an import remembers.</summary>
public sealed class SpotifyImportsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rambutan-spotify-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void The_export_zip_gives_liked_songs_then_the_playlists()
    {
        var path = Path.Combine(_dir.FullName, "my_spotify_data.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, string json)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(json);
            }
            Add("Spotify Account Data/Playlist1.json", """
                {"playlists":[{"name":"Rock","items":[{"track":{"trackName":"Panama","artistName":"Van Halen","albumName":"1984","trackUri":"spotify:track:4cOdK2wGLETKBW3PvgPWqT"}}]}]}
                """);
            Add("Spotify Account Data/YourLibrary.json", """
                {"tracks":[{"artist":"Daft Punk","album":"Discovery","track":"One More Time","uri":"spotify:track:0DiWol3AO6WpXZgp0goxAV"}]}
                """);
            Add("Spotify Account Data/StreamingHistory_music_0.json", "[]");
        }

        var lists = SpotifyImports.Read(path);

        Assert.Equal([ImportedListKind.LikedSongs, ImportedListKind.Playlist], lists.Select(l => l.Kind));
        Assert.Equal("One More Time", lists[0].Tracks[0].Title);
        Assert.Equal("Rock", lists[1].Name);
    }

    [Fact]
    public void A_csv_is_named_after_its_file()
    {
        var path = Write("Road trip.csv", "\"Track Name\",\"Artist Name(s)\"\n\"Panama\",\"Van Halen\"\n");

        var list = Assert.Single(SpotifyImports.Read(path));

        Assert.Equal("Road trip", list.Name);
        Assert.Equal(["Van Halen"], list.Tracks[0].Artists);
    }

    [Fact]
    public void A_zip_without_lists_or_a_file_without_titles_is_refused()
    {
        var zipPath = Path.Combine(_dir.FullName, "other.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");

        Assert.Equal(ImportProblem.Unreadable, Assert.Throws<ImportFormatException>(() => SpotifyImports.Read(zipPath)).Problem);
        Assert.Equal(ImportProblem.NoTitleColumn,
            Assert.Throws<ImportFormatException>(() => SpotifyImports.Read(Write("x.csv", "a,b\n1,2\n"))).Problem);
    }

    private static ImportRecord Record(string name) => new(
        name, ImportedListKind.Playlist, ImportTarget.YouTubeMusic, name,
        [new ImportedPart("PL1", 2)], ["a", "b"], DateTimeOffset.UnixEpoch);

    [Fact]
    public void The_records_are_kept_and_a_newer_file_is_left_as_it_is()
    {
        var store = new SpotifyImportRecordStore(_dir.FullName);
        store.Save([Record("Rock"), Record("Pop") with { ListKind = ImportedListKind.LikedSongs, Target = ImportTarget.ThisComputer }]);

        var read = new SpotifyImportRecordStore(_dir.FullName).Load();
        Assert.Equal(2, read.Count);
        Assert.Equal(["a", "b"], read[0].Keys);
        Assert.Equal(new ImportedPart("PL1", 2), Assert.Single(read[0].Parts));
        Assert.Equal((ImportedListKind.LikedSongs, ImportTarget.ThisComputer), (read[1].ListKind, read[1].Target));

        const string newer = """{"version":2,"imports":[]}""";
        File.WriteAllText(store.Path, newer);
        var later = new SpotifyImportRecordStore(_dir.FullName);
        Assert.Empty(later.Load());
        later.Save([Record("Rock")]);
        Assert.Equal(newer, File.ReadAllText(store.Path));
    }
}

/// <summary>The import page: matching, reviewing, and creating the playlists.</summary>
public sealed class SpotifyImportViewModelTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rambutan-spotify-vm-");
    private readonly FakeCatalog _catalog = new();
    private readonly List<string> _searched = [];
    private readonly LocalPlaylistLibrary _local = new(new MemoryStore());
    private bool _signedIn = true;

    public void Dispose() => _dir.Delete(recursive: true);

    private sealed class MemoryStore : ILocalPlaylistStore
    {
        public LoadedLocalPlaylists Load() => new([]);
        public void Save(IReadOnlyList<LocalPlaylist> playlists) { }
    }

    private static readonly ImportedTrack Panama = new("Panama", ["Van Halen"]);
    private static readonly ImportedTrack Jump = new("Jump", ["Van Halen"]);
    private static readonly ImportedTrack Lost = new("Lost", ["Nobody"]);
    private static readonly ImportedTrack Unchained = new("Unchained", ["Van Halen"]);

    private static TrackInfo Song(string title, string id) =>
        new() { VideoId = id, Title = title, Artists = [new ArtistRef("Van Halen")] };

    private Task<IReadOnlyList<TrackInfo>> Search(string query, CancellationToken ct)
    {
        _searched.Add(query);
        IReadOnlyList<TrackInfo> results = query switch
        {
            "Van Halen Panama" => [Song("Panama", "panama"), Song("Panama (Live)", "live")],
            "Van Halen Jump" => [Song("Jump", "jump")],
            "Van Halen Unchained" => [Song("Unchained", "unchained")],
            _ when query.StartsWith("Van Halen T", StringComparison.Ordinal) => [Song(query[10..], query[10..].ToLowerInvariant())],
            _ => [],
        };
        return Task.FromResult(results);
    }

    private SpotifyImportViewModel Page() =>
        new(new SpotifyImports(_dir.FullName, Search, spacing: TimeSpan.Zero), _catalog, _local, () => _signedIn);

    private static ImportedList Rock(params ImportedTrack[] tracks) =>
        new(ImportedListKind.Playlist, "Rock", tracks.Length > 0 ? tracks : [Panama, Jump, Panama, Lost]);

    private static async Task ChooseAndFindAsync(SpotifyImportViewModel page, params ImportedList[] lists)
    {
        page.Show("export.zip", lists);
        foreach (var item in page.Lists)
            item.IsChosen = true;
        await page.FindSongsCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Each_song_is_searched_once_and_the_list_says_what_it_adds()
    {
        var page = Page();

        await ChooseAndFindAsync(page, Rock());

        Assert.Equal(["Van Halen Panama", "Van Halen Jump", "Nobody Lost"], _searched);
        Assert.False(page.HasUnsearched);
        Assert.True(page.CanCreate);
        Assert.Equal(4, page.Rows.Count);
        Assert.Equal("Panama · Van Halen", page.Rows[0].Outcome);
        Assert.False(page.Rows[3].HasSong);

        page.OnlyProblems = true;
        Assert.Equal([Lost], page.Rows.Select(r => r.Source));
    }

    [Fact]
    public async Task Creating_on_YouTube_Music_makes_a_private_playlist_with_its_duplicates()
    {
        var page = Page();
        await ChooseAndFindAsync(page, Rock());

        await page.CreateCommand.ExecuteAsync(null);

        Assert.Equal([("Rock", PlaylistPrivacy.Private)], _catalog.CreatedPlaylists);
        var added = Assert.Single(_catalog.Added);
        Assert.Equal(("PL1", true), (added.PlaylistId, added.KeepDuplicates));
        Assert.Equal(["panama", "jump", "panama"], added.VideoIds);
        Assert.Null(page.Problem);
        var record = Assert.Single(new SpotifyImportRecordStore(_dir.FullName).Load());
        Assert.Equal(4, record.Keys.Count);
    }

    [Fact]
    public async Task The_users_changes_decide_what_is_added()
    {
        var page = Page();
        await ChooseAndFindAsync(page, Rock());
        var panama = page.Rows[0];
        await panama.ChooseAnotherCommand.ExecuteAsync(null);
        Assert.Equal(["panama", "live"], panama.Others.Select(s => s.VideoId));

        panama.PickCommand.Execute(panama.Others[1]);
        page.Rows[1].LeaveOutCommand.Execute(null);
        Assert.True(page.Rows[2].IsProblem);
        Assert.True(page.Rows[1].IsLeftOut);
        await page.CreateCommand.ExecuteAsync(null);

        Assert.Equal(["live", "live"], Assert.Single(_catalog.Added).VideoIds);
    }

    [Fact]
    public async Task Importing_again_adds_only_the_new_songs_to_the_earlier_playlist()
    {
        var first = Page();
        first.Show("export.zip", [Rock()]);
        first.Lists[0].IsChosen = true;
        await first.FindSongsCommand.ExecuteAsync(null);
        await first.CreateCommand.ExecuteAsync(null);
        Assert.True(first.Lists[0].HasEarlier);
        Assert.Empty(first.Rows);
        Assert.False(first.CanCreate);
        _catalog.Added.Clear();
        _searched.Clear();

        var again = Page();
        await ChooseAndFindAsync(again, Rock(Panama, Jump, Panama, Lost, Unchained));

        Assert.True(again.Lists[0].HasEarlier);
        Assert.Equal([Unchained], again.Rows.Select(r => r.Source));
        Assert.Equal(["Van Halen Unchained"], _searched);
        await again.CreateCommand.ExecuteAsync(null);

        Assert.Single(_catalog.CreatedPlaylists);
        var added = Assert.Single(_catalog.Added);
        Assert.Equal("PL1", added.PlaylistId);
        Assert.Equal(["unchained"], added.VideoIds);
        var record = Assert.Single(new SpotifyImportRecordStore(_dir.FullName).Load());
        Assert.Equal(4, record.Parts[0].Count);
        Assert.Equal(5, record.Keys.Count);
    }

    [Fact]
    public async Task Signed_out_the_lists_become_playlists_on_this_computer()
    {
        _signedIn = false;
        var page = Page();
        Assert.False(page.ToYouTubeMusic);
        page.ToYouTubeMusic = true;
        Assert.False(page.ToYouTubeMusic);

        await ChooseAndFindAsync(page, Rock());
        await page.CreateCommand.ExecuteAsync(null);

        Assert.Empty(_catalog.CreatedPlaylists);
        var playlist = Assert.Single(_local.All);
        Assert.Equal("Rock", playlist.Name);
        Assert.Equal(3, playlist.Items.Count);

        var again = Page();
        await ChooseAndFindAsync(again, Rock(Panama, Jump, Panama, Lost, Panama));
        await again.CreateCommand.ExecuteAsync(null);

        Assert.Equal(["panama", "jump", "panama", "panama"], Assert.Single(_local.All).Items.Select(i => i.Track.VideoId));
    }

    [Fact]
    public async Task A_list_that_fails_stops_the_import_and_keeps_the_playlist_it_made()
    {
        var page = Page();
        var pop = new ImportedList(ImportedListKind.Playlist, "Pop", [Jump]);
        var jazz = new ImportedList(ImportedListKind.Playlist, "Jazz", [Jump]);
        await ChooseAndFindAsync(page, Rock(), pop, jazz);
        _catalog.AddFailure = id => id == "PL2" ? new HttpRequestException("offline") : null;

        await page.CreateCommand.ExecuteAsync(null);

        Assert.Contains("Pop", page.Problem);
        Assert.Equal(2, _catalog.CreatedPlaylists.Count);
        var records = new SpotifyImportRecordStore(_dir.FullName).Load();
        Assert.Equal(["Rock", "Pop"], records.Select(r => r.ListName));
        Assert.Equal(new ImportedPart("PL2", 0), Assert.Single(records[1].Parts));
        Assert.Empty(records[1].Keys);
    }

    // The second batch of 100 fails, or the first; a second import adds what
    // is left to the same playlist.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task An_import_that_stopped_partway_adds_the_rest_and_nothing_twice(int failingAdd)
    {
        var rows = Enumerable.Range(1, 150).Select(n => new ImportedTrack($"T{n}", ["Van Halen"])).ToArray();
        var adds = 0;
        _catalog.AddFailure = _ => ++adds == failingAdd ? new HttpRequestException("offline") : null;
        var page = Page();
        await ChooseAndFindAsync(page, Rock(rows));
        await page.CreateCommand.ExecuteAsync(null);
        Assert.NotNull(page.Problem);
        var done = (failingAdd - 1) * ImportPlan.AddBatch;
        Assert.Equal(done, _catalog.Added.Sum(a => a.VideoIds.Count));

        var again = Page();
        await ChooseAndFindAsync(again, Rock(rows));
        Assert.Equal(150 - done, again.Rows.Count);
        await again.CreateCommand.ExecuteAsync(null);

        Assert.Null(again.Problem);
        Assert.Single(_catalog.CreatedPlaylists);
        var ids = _catalog.Added.SelectMany(a => a.VideoIds).ToList();
        Assert.Equal(150, ids.Count);
        Assert.Equal(150, ids.Distinct().Count());
        Assert.All(_catalog.Added, a => Assert.Equal("PL1", a.PlaylistId));
        Assert.Equal(150, Assert.Single(new SpotifyImportRecordStore(_dir.FullName).Load()).Parts[0].Count);
    }
}
