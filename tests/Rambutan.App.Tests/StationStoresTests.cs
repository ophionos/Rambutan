using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Stations;

namespace Rambutan.App.Tests;

/// <summary>
/// The saved Last.fm lists and search outcomes: versioned files in the
/// profile's folder, never another user's lists, and never a crash on a bad file.
/// </summary>
public sealed class StationStoresTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-stations-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TrackInfo Song(string title, string artist, string id) => new()
    {
        VideoId = id, Title = title, Artists = [new ArtistRef(artist, "UC1")], Duration = TimeSpan.FromSeconds(200),
        ThumbnailUrl = "https://example.invalid/a.jpg",
    };

    private static readonly StationRow[] Rows =
    [
        new(new StationTrack("Daft Punk", "Instant Crush", TimeSpan.FromSeconds(337), "lf1"), StationMatch.Song,
            Song("Instant Crush", "Daft Punk", "ytm1")),
        new(new StationTrack("Holy Ghost!", "Okay", null, "lf2"), StationMatch.ServiceVideo,
            Song("Okay", "Holy Ghost!", "lf2")),
        new(new StationTrack("Nobody", "Nothing"), StationMatch.NotFound, null),
    ];

    [Fact]
    public void A_saved_list_reads_back_with_every_row()
    {
        var store = new StationSnapshotStore(_dir);
        var at = new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.Zero);
        store.Save("Listener", [new StationSnapshot(StationKind.Mix, at, Rows)]);

        var saved = Assert.Single(store.Load("listener"));
        Assert.Equal(StationKind.Mix, saved.Kind);
        Assert.Equal(at, saved.LoadedAt);
        Assert.Equal([StationMatch.Song, StationMatch.ServiceVideo, StationMatch.NotFound], saved.Rows.Select(r => r.Match));
        Assert.Equal("ytm1", saved.Rows[0].Track!.VideoId);
        Assert.Equal("UC1", saved.Rows[0].Track!.Artists[0].BrowseId);
        Assert.Equal(TimeSpan.FromSeconds(337), saved.Rows[0].Source.Duration);
        Assert.Equal("lf2", saved.Rows[1].Source.VideoId);
        Assert.Null(saved.Rows[2].Track);
        Assert.Contains("\"version\":1", File.ReadAllText(store.Path));
    }

    [Fact]
    public void Another_users_lists_are_not_shown()
    {
        var store = new StationSnapshotStore(_dir);
        store.Save("someone", [new StationSnapshot(StationKind.Mix, DateTimeOffset.UtcNow, Rows)]);
        Assert.Empty(store.Load("listener"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "version": 2, "user": "listener", "stations": [ { "kind": "mix", "rows": [] } ] }""")]
    [InlineData("""{ "version": 1, "user": "listener", "stations": [ { "kind": "radio", "rows": [] } ] }""")]
    public void A_bad_newer_or_unknown_file_reads_as_nothing(string json)
    {
        Directory.CreateDirectory(_dir);
        var store = new StationSnapshotStore(_dir);
        File.WriteAllText(store.Path, json);
        Assert.Empty(store.Load("listener"));
    }

    [Fact]
    public void A_row_that_says_it_plays_without_a_track_reads_as_not_found()
    {
        Directory.CreateDirectory(_dir);
        var store = new StationSnapshotStore(_dir);
        File.WriteAllText(store.Path, """
            { "version": 1, "user": "listener", "stations": [ { "kind": "mix", "loadedAt": "2026-10-10T09:00:00Z", "rows": [
              { "artist": "A", "title": "B", "match": "song" },
              { "title": "no artist", "match": "notFound" } ] } ] }
            """);
        var row = Assert.Single(Assert.Single(store.Load("listener")).Rows);
        Assert.Equal(StationMatch.NotFound, row.Match);
    }

    [Fact]
    public void Search_outcomes_survive_a_restart()
    {
        var time = new ManualTime();
        var cache = new StationMatchCache(time);
        cache.Put(new StationTrack("Daft Punk", "Instant Crush"),
            new StationSearchOutcome(Song("Instant Crush", "Daft Punk", "ytm1"), null, time.Now));
        cache.Put(new StationTrack("The Courteeners", "That Kiss"),
            new StationSearchOutcome(null, Song("That Kiss (Live)", "The Courteeners", "live"), time.Now));
        new StationMatchStore(_dir, cache).Save();

        var restored = new StationMatchCache(time);
        new StationMatchStore(_dir, restored).Load();

        Assert.True(restored.TryGet(new StationTrack("daft punk", "instant crush"), out var song));
        Assert.Equal("ytm1", song.Song!.VideoId);
        Assert.True(restored.TryGet(new StationTrack("The Courteeners", "That Kiss"), out var other));
        Assert.Null(other.Song);
        Assert.Equal("live", other.OtherVersion!.VideoId);
    }

    [Fact]
    public void Outcomes_from_other_matcher_rules_are_dropped()
    {
        Directory.CreateDirectory(_dir);
        var cache = new StationMatchCache();
        var store = new StationMatchStore(_dir, cache);
        File.WriteAllText(store.Path, $$"""
            { "version": 1, "rules": {{TrackMatcher.RulesVersion + 1}}, "entries": [
              { "key": "{{StationMatchCache.KeyOf(new StationTrack("A", "B")).Replace("\u001f", "\\u001f")}}",
                "searchedAt": "{{DateTimeOffset.UtcNow:O}}", "song": { "videoId": "x", "title": "B" } } ] }
            """);
        store.Load();
        Assert.False(cache.TryGet(new StationTrack("A", "B"), out _));

        File.WriteAllText(store.Path, File.ReadAllText(store.Path)
            .Replace($"\"rules\": {TrackMatcher.RulesVersion + 1}", $"\"rules\": {TrackMatcher.RulesVersion}"));
        store.Load();
        Assert.True(cache.TryGet(new StationTrack("A", "B"), out _));
    }

    [Fact]
    public void A_bad_matches_file_is_ignored()
    {
        Directory.CreateDirectory(_dir);
        var store = new StationMatchStore(_dir, new StationMatchCache());
        File.WriteAllText(store.Path, "{");
        store.Load();
    }
}

/// <summary>Home shows the saved lists first and asks Last.fm only when they
/// are old, Home is refreshed, or there are none.</summary>
public sealed class HomeStationsSavedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-stations-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // A shelf a test didn't wait for can still be saving on the thread pool
        // (HomeStations chains its saves there), and a file it writes mid-delete
        // makes the delete fail with "directory not empty". Seen on Linux CI.
        for (var attempt = 1; Directory.Exists(_dir); attempt++)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualTime _time = new();
    private int _searches;

    private (HomeStations Stations, HomeStationsTests.FakeStations Source) Make(string user = "listener", bool offline = false)
    {
        var source = new HomeStationsTests.FakeStations();
        var cache = new StationMatchCache(_time);
        var stations = new HomeStations(
            source,
            new StationResolver((query, _) =>
            {
                Interlocked.Increment(ref _searches);
                if (offline)
                    throw new HttpRequestException("offline");
                return Task.FromResult<IReadOnlyList<TrackInfo>>(query.StartsWith("Daft Punk", StringComparison.Ordinal)
                    ? [new TrackInfo { VideoId = "ytm-crush", Title = "Instant Crush", Artists = [new ArtistRef("Daft Punk")] }]
                    : []);
            }, cache: cache),
            () => user,
            new StationSnapshotStore(_dir),
            new StationMatchStore(_dir, cache),
            _time) { Enabled = true };
        return (stations, source);
    }

    private static StationShelfViewModel Shelf(HomeStations stations) =>
        new(StationKind.Mix, stations, (_, _) => Task.CompletedTask);

    [Fact]
    public async Task A_recent_saved_list_shows_without_asking_Last_fm()
    {
        await Shelf(Make().Stations).LoadAsync();

        var (stations, source) = Make();
        var shelf = Shelf(stations);
        await shelf.ShowAsync();

        Assert.Empty(source.Calls);
        Assert.Equal(["ytm-crush", "lf-okay"], shelf.Shelf!.Entries.Cast<SongEntry>().Select(e => e.Track.VideoId));
        Assert.Equal(3, shelf.Rows.Count);
        Assert.Equal(_time.Now, shelf.LoadedAt);
    }

    [Fact]
    public async Task An_old_saved_list_shows_and_is_replaced()
    {
        await Shelf(Make().Stations).LoadAsync();
        _time.Now += HomeStations.Freshness + TimeSpan.FromMinutes(1);

        var (stations, source) = Make();
        var shelf = Shelf(stations);
        await shelf.ShowAsync();

        Assert.Single(source.Calls);
        Assert.Equal(_time.Now, shelf.LoadedAt);
    }

    [Fact]
    public async Task A_known_track_is_not_searched_again_after_a_restart()
    {
        await Shelf(Make().Stations).LoadAsync();
        var first = _searches;

        await Shelf(Make().Stations).LoadAsync();

        Assert.Equal(3, first);
        Assert.Equal(3, _searches);
    }

    [Fact]
    public async Task An_artist_station_keeps_the_saved_search_outcomes()
    {
        await Shelf(Make().Stations).LoadAsync();
        Assert.Equal(3, _searches);

        // After a restart, an artist's station is the first load: its save must
        // add to the saved outcomes, not replace them.
        var (stations, source) = Make();
        source.ArtistAnswer = _ => [new("Mr. Oizo", "Positif")];
        await stations.LoadArtistAsync("Mr. Oizo");
        Assert.Equal(4, _searches);

        await Shelf(Make().Stations).LoadAsync();
        Assert.Equal(4, _searches);
        // The artist's list itself isn't saved.
        Assert.Equal([StationKind.Mix], new StationSnapshotStore(_dir).Load("listener").Select(s => s.Kind));
    }

    [Fact]
    public async Task Another_users_list_is_not_shown()
    {
        await Shelf(Make("someone").Stations).LoadAsync();

        var (stations, source) = Make("listener");
        var shelf = Shelf(stations);
        Assert.Null(stations.Saved(StationKind.Mix));
        await shelf.ShowAsync();
        Assert.Single(source.Calls);
    }

    [Fact]
    public async Task When_Home_fails_the_saved_lists_still_show()
    {
        await Shelf(Make().Stations).LoadAsync();

        var (stations, source) = Make(offline: true);
        var catalog = new FakeCatalog();
        var home = new HomePageViewModel(catalog, null!, stations, (_, _) => Task.CompletedTask);
        var load = home.EnsureLoadedAsync();
        catalog.HomeCalls[0].Fail("offline");
        await load;

        Assert.NotNull(home.LoadError);
        var mix = Assert.IsType<StationShelfViewModel>(home.Items[0]);
        Assert.Equal(StationKind.Mix, mix.Kind);
        Assert.NotNull(mix.Shelf);
        // Only the mix was saved; the recommendations try to load, and say why they can't.
        Assert.DoesNotContain(source.Calls, c => c.Kind == StationKind.Mix);
    }

    [Fact]
    public async Task When_Home_fails_with_nothing_saved_the_failure_takes_the_page()
    {
        var (stations, source) = Make(offline: true);
        var catalog = new FakeCatalog();
        var home = new HomePageViewModel(catalog, null!, stations, (_, _) => Task.CompletedTask);
        var load = home.EnsureLoadedAsync();
        catalog.HomeCalls[0].Fail("offline");
        await load;

        Assert.Empty(home.Items);
        Assert.Empty(source.Calls);
    }

    [Fact]
    public async Task Offline_the_saved_list_stays_and_says_how_old_it_is()
    {
        await Shelf(Make().Stations).LoadAsync();
        _time.Now += HomeStations.Freshness + TimeSpan.FromMinutes(1);

        var (stations, source) = Make(offline: true);
        // New tracks, so the saved outcomes can't answer and searches have to run.
        source.Answer = _ => [new("Pulp", "Babies", VideoId: "lf-babies")];
        var shelf = Shelf(stations);
        await shelf.ShowAsync();

        Assert.Equal(2, shelf.Shelf!.Entries.Count);
        Assert.NotNull(shelf.LoadError);
        Assert.NotNull(shelf.SavedNotice);
        Assert.Equal(_time.Now - HomeStations.Freshness - TimeSpan.FromMinutes(1), shelf.LoadedAt);
    }
}
