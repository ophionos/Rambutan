using System.Text;
using System.Text.Json.Nodes;
using Rambutan.Core.Downloads;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Settings;

namespace Rambutan.LocalLibrary.Tests;

public sealed class DownloadStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));

    private string DataDir => Path.Combine(_root, "data");
    private string Folder => Path.Combine(_root, "data", "cache", "downloads");

    private readonly List<string> _watched = [];
    private LocalFileIndex? _index;
    private TextReader? _reader;

    /// <summary>A new index on the data folder, as at a start; the one before
    /// is disposed first, so its last save can't land after this one's.</summary>
    private LocalFileIndex NewIndex()
    {
        _index?.Dispose();
        _reader = new TextReader();
        return _index = new LocalFileIndex(DataDir, _reader, _ => Task.CompletedTask, () => _watched);
    }

    public void Dispose()
    {
        _index?.Dispose();
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
        Artists = [new ArtistRef("Artist", "UC1")],
        Album = "Album",
        Duration = TimeSpan.FromSeconds(200),
        ThumbnailUrl = "https://img.test/a.jpg",
    };

    private DownloadStore NewStore(IAudioFetcher? fetcher = null, string? folder = null) =>
        new(NewIndex(), DataDir, () => folder ?? Folder, fetcher ?? new BytesFetcher("audio"));

    private static readonly IProgress<DownloadProgress> NoProgress = new Progress<DownloadProgress>();

    /// <summary>A request of its own, made now.</summary>
    private static DownloadOrder Now => new(DateTimeOffset.UtcNow, 0);

    [Fact]
    public async Task A_download_is_kept_with_its_record_and_plays_with_its_loudness()
    {
        using var store = NewStore(new BytesFetcher("audio", loudness: -4.5));
        await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);

        var media = store.TryGetMedia("vid00000001");
        Assert.NotNull(media);
        Assert.Equal("audio", File.ReadAllText(media.Url));
        Assert.EndsWith(".webm", media.Url);
        Assert.Equal(-4.5, media.LoudnessDb);
        Assert.True(store.Contains("vid00000001"));
        Assert.Equal(5, store.TotalBytes);
        Assert.Empty(Directory.EnumerateFiles(Folder, "*.part"));
    }

    [Fact]
    public async Task A_record_round_trips_through_the_local_index()
    {
        using (var store = NewStore())
            await store.DownloadAsync(Track("vid00000001") with { TrackNumber = 7, SongId = "aud00000001" }, Now, NoProgress, default);

        var json = File.ReadAllText(Path.Combine(DataDir, "local-index.json"));
        Assert.Contains("\"version\":1", json);
        Assert.Contains("\"songId\":\"aud00000001\"", json);
        Assert.False(File.Exists(Path.Combine(DataDir, "downloads.json")));

        using var reloaded = NewStore();
        var download = Assert.Single(reloaded.List());
        Assert.Equal("Song vid00000001", download.Track.Title);
        Assert.Equal("Artist", download.Track.ArtistDisplay);
        Assert.Equal(TimeSpan.FromSeconds(200), download.Track.Duration);
        Assert.Equal(7, download.Track.TrackNumber);
        Assert.Equal("aud00000001", download.Track.SongId);
        Assert.Equal(5, download.SizeBytes);
        Assert.False(download.IsMissing);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"version":99,"downloads":[]}""")]
    public void An_unreadable_manifest_fails_closed_and_is_set_aside(string content)
    {
        Directory.CreateDirectory(DataDir);
        var path = Path.Combine(DataDir, "downloads.json");
        File.WriteAllText(path, content);

        using var store = NewStore();

        Assert.Empty(store.List());
        Assert.False(File.Exists(path));
        Assert.Equal(content, File.ReadAllText(path + ".unreadable"));
    }

    [Fact]
    public async Task A_failed_fetch_leaves_nothing_behind()
    {
        using var store = NewStore(new BytesFetcher("partial", fail: true));

        await Assert.ThrowsAsync<IOException>(() => store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default));

        Assert.False(store.Contains("vid00000001"));
        Assert.Empty(Directory.EnumerateFiles(Folder));
        Assert.False(File.Exists(Path.Combine(DataDir, "downloads.json")));
    }

    [Fact]
    public async Task Delete_removes_the_file_and_the_record()
    {
        using var store = NewStore();
        await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);
        var path = store.TryGetMedia("vid00000001")!.Url;

        store.Delete("vid00000001");

        Assert.False(File.Exists(path));
        Assert.Empty(store.List());
        using var reloaded = NewStore();
        Assert.Empty(reloaded.List());
    }

    [Fact]
    public async Task A_file_in_use_is_deleted_once_it_is_let_go()
    {
        using var store = NewStore();
        await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);
        var path = store.TryGetMedia("vid00000001")!.Url;
        store.DeleteFile = _ => false; // the decoder holds it open

        store.Delete("vid00000001");

        Assert.Empty(store.List());
        Assert.False(store.Contains("vid00000001"));
        Assert.True(File.Exists(path));
        Assert.Equal(5, store.TotalBytes); // it still takes the space

        store.RetryLeftovers(); // still held
        Assert.True(File.Exists(path));

        store.DeleteFile = p => { File.Delete(p); return true; }; // playback moved on
        store.RetryLeftovers();
        Assert.False(File.Exists(path));
        Assert.Equal(0, store.TotalBytes);
    }

    [Fact]
    public async Task A_file_still_in_use_at_exit_is_deleted_at_the_next_start()
    {
        string path;
        using (var store = NewStore())
        {
            await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);
            path = store.TryGetMedia("vid00000001")!.Url;
            store.DeleteFile = _ => false;
            store.Delete("vid00000001");
        }
        Assert.True(File.Exists(path));

        using var reloaded = NewStore();

        Assert.False(File.Exists(path));
        Assert.Empty(reloaded.List());
        Assert.DoesNotContain("downloadLeftovers", File.ReadAllText(Path.Combine(DataDir, "local-index.json")));
    }

    [Fact]
    public async Task A_leftover_downloaded_again_is_kept()
    {
        using var store = NewStore();
        await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);
        store.DeleteFile = _ => false;
        store.Delete("vid00000001");
        store.DeleteFile = p => { File.Delete(p); return true; };

        await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);
        store.RetryLeftovers();

        Assert.True(store.Contains("vid00000001"));
        using var reloaded = NewStore();
        Assert.True(reloaded.Contains("vid00000001"));
    }

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private DownloadStore StoreWithCovers(List<string> fetched, bool fail = false) =>
        new(NewIndex(), DataDir, () => Folder, new BytesFetcher("audio"), (url, _) =>
        {
            fetched.Add(url);
            return fail ? Task.FromException<byte[]>(new HttpRequestException("offline")) : Task.FromResult(Jpeg);
        });

    [Fact]
    public async Task A_download_keeps_its_cover_beside_its_audio_and_deletes_it_with_it()
    {
        var fetched = new List<string>();
        using var store = StoreWithCovers(fetched);
        await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);

        var cover = store.ArtworkPathFor("vid00000001");
        Assert.Equal(Path.Combine(Folder, "vid00000001.jpg"), cover);
        Assert.Equal(Jpeg, File.ReadAllBytes(cover!));
        Assert.Equal(["https://img.test/a.jpg"], fetched);
        using (var reloaded = NewStore())
            Assert.Equal(cover, reloaded.ArtworkPathFor("vid00000001"));

        store.Delete("vid00000001");

        Assert.False(File.Exists(cover));
        Assert.Null(store.ArtworkPathFor("vid00000001"));
    }

    [Fact]
    public async Task A_cover_that_cannot_be_fetched_does_not_fail_the_download()
    {
        using var store = StoreWithCovers([], fail: true);

        await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);

        Assert.True(store.Contains("vid00000001"));
        Assert.Null(store.ArtworkPathFor("vid00000001"));
        Assert.Empty(Directory.EnumerateFiles(Folder, "*.part"));
    }

    [Fact]
    public async Task Downloads_without_a_cover_get_one_later()
    {
        using (var old = NewStore()) // made before covers were kept
            await old.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);
        var fetched = new List<string>();
        using var store = StoreWithCovers(fetched);
        Assert.Null(store.ArtworkPathFor("vid00000001"));

        await store.FillMissingArtworkAsync(default);

        Assert.NotNull(store.ArtworkPathFor("vid00000001"));
        await store.FillMissingArtworkAsync(default);
        Assert.Single(fetched); // nothing left to fetch
    }

    [Fact]
    public async Task Filling_covers_stops_when_every_fetch_fails()
    {
        using (var old = NewStore())
        {
            for (var i = 1; i <= 5; i++)
                await old.DownloadAsync(Track($"vid0000000{i}"), Now, NoProgress, default);
        }
        var fetched = new List<string>();
        using var store = StoreWithCovers(fetched, fail: true);

        await store.FillMissingArtworkAsync(default);

        Assert.Equal(3, fetched.Count);
    }

    [Fact]
    public async Task Changing_the_folder_keeps_existing_downloads_where_they_are()
    {
        var folder = Folder;
        using var store = new DownloadStore(NewIndex(), DataDir, () => folder, new BytesFetcher("audio"));
        await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);

        folder = Path.Combine(_root, "elsewhere");
        await store.DownloadAsync(Track("vid00000002"), Now, NoProgress, default);

        Assert.StartsWith(Folder, store.TryGetMedia("vid00000001")!.Url);
        Assert.StartsWith(folder, store.TryGetMedia("vid00000002")!.Url);
    }

    [Fact]
    public async Task A_missing_file_is_reported_and_never_played()
    {
        using var store = NewStore();
        await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);
        File.Delete(store.TryGetMedia("vid00000001")!.Url);

        Assert.True(Assert.Single(store.List()).IsMissing);
        Assert.False(store.Contains("vid00000001"));
        Assert.Null(store.TryGetMedia("vid00000001"));
    }

    [Fact]
    public void Pending_jobs_survive_a_restart()
    {
        using (var store = NewStore())
        {
            store.SavePending([
                new PendingDownload(Track("vid00000001"), new DownloadOrder(Album, 0)),
                new PendingDownload(Track("vid00000002"), new DownloadOrder(Album, 1)),
            ]);
        } // Dispose flushes the delayed save

        using var reloaded = NewStore();
        var pending = reloaded.LoadPending();
        Assert.Equal(["vid00000001", "vid00000002"], pending.Select(p => p.Track.VideoId));
        Assert.Equal([new DownloadOrder(Album, 0), new DownloadOrder(Album, 1)], pending.Select(p => p.Order));
    }

    private static readonly DateTimeOffset Album = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_list_keeps_each_request_in_order_and_puts_the_newest_request_first()
    {
        using var store = NewStore();
        var single = Album.AddMinutes(5);
        // The album's tracks finish out of order, as parallel jobs do.
        await store.DownloadAsync(Track("vid00000003"), new DownloadOrder(Album, 2), NoProgress, default);
        await store.DownloadAsync(Track("vid00000001"), new DownloadOrder(Album, 0), NoProgress, default);
        await store.DownloadAsync(Track("vid00000009"), new DownloadOrder(single, 0), NoProgress, default);
        await store.DownloadAsync(Track("vid00000002"), new DownloadOrder(Album, 1), NoProgress, default);

        Assert.Equal(
            ["vid00000009", "vid00000001", "vid00000002", "vid00000003"],
            store.List().Select(d => d.Track.VideoId));
        using var reloaded = NewStore();
        Assert.Equal(
            ["vid00000009", "vid00000001", "vid00000002", "vid00000003"],
            reloaded.List().Select(d => d.Track.VideoId));
    }

    [Fact]
    public void Records_saved_before_the_order_was_kept_sort_by_when_they_finished()
    {
        Directory.CreateDirectory(Folder);
        string Record(string id, string at)
        {
            var path = Path.Combine(Folder, id + ".webm").Replace("\\", "\\\\");
            File.WriteAllText(Path.Combine(Folder, id + ".webm"), "audio");
            return $$"""{"videoId":"{{id}}","title":"T","path":"{{path}}","downloadedAt":"{{at}}"}""";
        }
        File.WriteAllText(Path.Combine(DataDir, "downloads.json"),
            $$"""{"version":1,"downloads":[{{Record("vid00000001", "2026-10-01T00:00:00Z")}},{{Record("vid00000002", "2026-10-02T00:00:00Z")}}]}""");

        using var store = NewStore();

        Assert.Equal(["vid00000002", "vid00000001"], store.List().Select(d => d.Track.VideoId));
    }

    [Fact]
    public void Stale_part_files_are_cleaned_at_start_but_other_files_are_not()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "vid00000001.part"), "x");
        File.WriteAllText(Path.Combine(Folder, "notes.txt"), "the user's");

        using var store = NewStore();

        Assert.Equal(["notes.txt"], Directory.EnumerateFiles(Folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Playback_prefers_the_download_but_the_fault_retry_streams_without_deleting_it()
    {
        using var store = NewStore();
        await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);
        var inner = new UrlSource();
        var source = new DownloadsFirstSource(inner, store);

        var first = await source.ResolveAsync("vid00000001");
        var retry = await source.ResolveAsync("vid00000001", bypassCache: true);

        Assert.Equal(store.TryGetMedia("vid00000001")!.Url, first.Url);
        Assert.Equal("https://stream.test/vid00000001", retry.Url);
        Assert.True(store.Contains("vid00000001"));
        Assert.True(source.IsWarm("vid00000001"));
    }

    private string Manifest => Path.Combine(DataDir, "downloads.json");

    private static string Json(string s) => s.Replace("\\", "\\\\");

    /// <summary>A downloads.json as builds before the merge wrote it, with the
    /// audio files it names.</summary>
    private string WriteManifest(string[] ids, string[]? leftovers = null)
    {
        Directory.CreateDirectory(Folder);
        var records = ids.Select(id =>
        {
            var path = Path.Combine(Folder, id + ".webm");
            File.WriteAllText(path, "audio");
            return $$"""{"videoId":"{{id}}","title":"Song {{id}}","path":"{{Json(path)}}","audioCodec":"opus","sizeBytes":5,"downloadedAt":"2026-10-01T00:00:00Z"}""";
        });
        var left = leftovers is null ? "" : $$""","leftoverPaths":[{{string.Join(",", leftovers.Select(p => $"\"{Json(p)}\""))}}]""";
        var json = $$"""{"version":1,"downloads":[{{string.Join(",", records)}}]{{left}}}""";
        File.WriteAllText(Manifest, json);
        return json;
    }

    [Fact]
    public void The_first_start_takes_in_downloads_json_and_leaves_it_as_it_was()
    {
        var leftover = Path.Combine(Folder, "vid00000009.webm");
        var json = WriteManifest(["vid00000001", "vid00000002"], [leftover]);
        File.WriteAllText(leftover, "old");

        using var store = NewStore();

        Assert.Equal(["vid00000001", "vid00000002"], store.List().Select(d => d.Track.VideoId).Order());
        Assert.Equal(Path.Combine(Folder, "vid00000001.webm"), store.TryGetMedia("vid00000001")!.Url);
        Assert.False(File.Exists(leftover));
        Assert.Equal(json, File.ReadAllText(Manifest));
        Assert.Contains("\"downloadsImported\":true", File.ReadAllText(Path.Combine(DataDir, "local-index.json")));
    }

    [Fact]
    public async Task Downloads_json_is_taken_in_once()
    {
        WriteManifest(["vid00000001"]);
        using (var store = NewStore())
        {
            await store.DownloadAsync(Track("vid00000002"), Now, NoProgress, default);
            store.Delete("vid00000001");
        }

        using var reloaded = NewStore();

        Assert.Equal(["vid00000002"], reloaded.List().Select(d => d.Track.VideoId));
    }

    [Fact]
    public async Task An_index_an_older_build_saved_takes_the_records_in_again_without_doubles()
    {
        // An older build reads the index, drops the fields it doesn't know and
        // saves it: the entries stay, the downloads and the flag go.
        WriteManifest(["vid00000001"]);
        using (var store = NewStore())
            await store.DownloadAsync(Track("vid00000002"), Now, NoProgress, default);
        var indexPath = Path.Combine(DataDir, "local-index.json");
        var saved = JsonNode.Parse(File.ReadAllText(indexPath))!.AsObject();
        saved.Remove("downloadsImported");
        foreach (var file in saved["files"]!.AsArray())
            file!.AsObject().Remove("download");
        File.WriteAllText(indexPath, saved.ToJsonString());

        using var reloaded = NewStore();

        // vid00000002 was made after the import, so downloads.json never had it.
        Assert.Equal(["vid00000001"], reloaded.List().Select(d => d.Track.VideoId));
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(indexPath))!["files"]!.AsArray().Count);
    }

    [Fact]
    public void Two_entries_for_one_video_keep_the_newer_download()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Folder);
        string Entry(string id, string file, string at)
        {
            var path = Path.Combine(Folder, file);
            File.WriteAllText(path, "audio");
            return $$$"""{"id":"{{{id}}}","path":"{{{Json(path)}}}","size":5,"modifiedAt":"{{{at}}}","unreadable":true,"download":{"videoId":"vid00000001","title":"T","audioCodec":"opus","sizeBytes":5,"downloadedAt":"{{{at}}}"}}""";
        }
        File.WriteAllText(Path.Combine(DataDir, "local-index.json"),
            $$"""{"version":1,"nextId":3,"downloadsImported":true,"files":[{{Entry("local:1", "old.webm", "2026-10-01T00:00:00Z")}},{{Entry("local:2", "new.webm", "2026-10-02T00:00:00Z")}},{{Entry("local:3", "older.webm", "2026-09-01T00:00:00Z")}}]}""");

        using var store = NewStore();

        Assert.Equal(Path.Combine(Folder, "new.webm"), store.TryGetMedia("vid00000001")!.Url);
        Assert.Single(store.List());
    }

    [Fact]
    public async Task A_download_onto_a_scanned_file_becomes_that_entry()
    {
        _watched.Add(Folder);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "vid00000001.webm"), "mine");
        using var store = NewStore();
        await _index!.ScanAsync();
        var scanned = Assert.Single(_index.Files());

        await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);

        Assert.Empty(_index.Files());
        var file = Assert.Single(_index.Downloads());
        Assert.Equal(scanned.Id, file.Id);
        Assert.Equal("vid00000001", file.Download?.Track.VideoId);
    }

    [Fact]
    public async Task A_download_in_a_watched_folder_is_read_by_a_scan_and_stays_a_download()
    {
        _watched.Add(Folder);
        using var store = NewStore();
        await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);
        Assert.Null(Assert.Single(_index!.Downloads()).Tags);

        await _index.ScanAsync();
        Assert.Equal("audio", Assert.Single(_index.Downloads()).Tags?.Title);

        var path = store.TryGetMedia("vid00000001")!.Url;
        File.WriteAllText(path, "changed");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        await _index.ScanAsync();

        Assert.Empty(_index.Files());
        var file = Assert.Single(_index.Downloads());
        Assert.Equal("changed", file.Tags?.Title);
        Assert.True(store.Contains("vid00000001"));
    }

    [Fact]
    public async Task A_download_deleted_while_a_scan_reads_it_stays_deleted()
    {
        _watched.Add(Folder);
        using var store = NewStore();
        await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);
        var reader = _reader!;
        reader.Hold = new ManualResetEventSlim();
        var scan = _index!.ScanAsync();
        Assert.True(reader.Reading.Wait(TimeSpan.FromSeconds(10)));

        store.DeleteFile = _ => false; // still open, so the read finishes
        store.Delete("vid00000001");
        reader.Hold.Set();
        await scan;

        Assert.False(store.Contains("vid00000001"));
        Assert.Empty(store.List());
        Assert.All(_index.Files(), f => Assert.Null(f.Download));
    }

    [Fact]
    public async Task Opening_a_downloaded_file_keeps_it_a_download()
    {
        using var store = NewStore();
        await store.DownloadAsync(Track("vid00000001"), Now, NoProgress, default);
        var path = store.TryGetMedia("vid00000001")!.Url;

        var opened = Assert.Single(await _index!.OpenAsync([path]));

        Assert.Equal("audio", opened.Tags?.Title);
        Assert.Equal("vid00000001", opened.Download?.Track.VideoId);
        Assert.True(store.Contains("vid00000001"));
    }

    [Fact]
    public async Task Downloads_finishing_together_during_a_scan_are_all_kept()
    {
        _watched.Add(Folder);
        Directory.CreateDirectory(Folder);
        for (var i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(Folder, $"mine{i}.mp3"), $"mine {i}");
        var ids = Enumerable.Range(1, 20).Select(i => $"vid{i:D8}").ToArray();
        using (var store = NewStore())
            await Task.WhenAll([_index!.ScanAsync(), .. ids.Select(id => Task.Run(() => store.DownloadAsync(Track(id), Now, NoProgress, default)))]);

        using var reloaded = NewStore();

        Assert.Equal(ids, reloaded.List().Select(d => d.Track.VideoId).Order());
        Assert.Equal(20, _index!.Files().Count(f => f.Download is null));
    }

    private sealed class BytesFetcher(string content, double? loudness = null, bool fail = false) : IAudioFetcher
    {
        public async Task<FetchedAudio> FetchAsync(
            TrackInfo track, Stream target, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            await target.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
            if (fail)
                throw new IOException("cut short");
            return new FetchedAudio("opus", "251", AudioQuality.High, loudness);
        }
    }

    /// <summary>Reads a file's text as its title.</summary>
    private sealed class TextReader : IMediaFileReader
    {
        /// <summary>When set, a read waits for it.</summary>
        public ManualResetEventSlim? Hold { get; set; }

        public ManualResetEventSlim Reading { get; } = new();

        public LocalFileTags? Read(string path)
        {
            Reading.Set();
            Hold?.Wait(TimeSpan.FromSeconds(10));
            return new("opus", Title: File.ReadAllText(path));
        }
    }

    private sealed class UrlSource : IStreamSource
    {
        public Task<ResolvedMedia> ResolveAsync(string videoId, bool bypassCache = false, CancellationToken ct = default) =>
            Task.FromResult(new ResolvedMedia($"https://stream.test/{videoId}", "opus"));
    }
}
