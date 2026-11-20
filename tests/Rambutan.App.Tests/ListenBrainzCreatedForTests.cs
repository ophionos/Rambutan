using Avalonia.Controls;
using Avalonia.VisualTree;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Models;
using Rambutan.Core.Stations;
using Rambutan.Integrations.ListenBrainz;
using Rambutan.Integrations.Scrobbling;

namespace Rambutan.App.Tests;

/// <summary>
/// The playlists ListenBrainz makes for the user (Daily Jams, Weekly Jams,
/// Weekly Exploration) as station shelves on Home: only a current one shows,
/// after Last.fm's shelves, behind a setting of their own that needs the
/// stations' ListenBrainz account.
/// </summary>
public sealed class ListenBrainzCreatedForTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-lb-createdfor-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Includes a directory the test never created.
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly CreatedForPlaylist Daily = new(CreatedForKind.DailyJams, "daily-1",
        new(2026, 11, 20, 6, 0, 0, TimeSpan.Zero), new(2026, 11, 21, 6, 0, 0, TimeSpan.Zero));

    private static readonly CreatedForPlaylist Weekly = new(CreatedForKind.WeeklyJams, "weekly-1",
        new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero));

    // ListenBrainz still lists it, but it expired before "now".
    private static readonly CreatedForPlaylist OldExploration = new(CreatedForKind.WeeklyExploration, "exploration-0",
        new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero));

    internal sealed class FakeListenBrainz : IArtistStationSource, ICreatedForSource
    {
        public Func<string, IReadOnlyList<CreatedForPlaylist>> List { get; set; } = _ => [OldExploration, Weekly, Daily];

        /// <summary>When set, the list waits for it.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public List<string> ListCalls { get; } = [];

        public Func<string, IReadOnlyList<StationTrack>> Playlist { get; set; } = _ =>
        [
            new("Daft Punk", "Instant Crush"),
            new("Nobody", "Nothing"),
        ];

        public List<string> PlaylistCalls { get; } = [];

        public async Task<IReadOnlyList<CreatedForPlaylist>> GetCreatedForAsync(string user, CancellationToken ct = default)
        {
            ListCalls.Add(user);
            if (Gate is { } gate)
                await gate.Task;
            return List(user);
        }

        public Task<IReadOnlyList<StationTrack>> GetPlaylistAsync(string id, CancellationToken ct = default)
        {
            PlaylistCalls.Add(id);
            return Task.FromResult(Playlist(id));
        }

        public Task<IReadOnlyList<StationTrack>> GetArtistStationAsync(ArtistSeed artist, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StationTrack>>([]);
    }

    private static Task<IReadOnlyList<TrackInfo>> Search(string query, CancellationToken _) =>
        Task.FromResult<IReadOnlyList<TrackInfo>>(query.StartsWith("Daft Punk", StringComparison.Ordinal)
            ? [new TrackInfo { VideoId = "ytm-crush", Title = "Instant Crush", Artists = [new ArtistRef("Daft Punk")] }]
            : []);

    private sealed class Setup
    {
        public FakeListenBrainz Source { get; } = new();
        public HomeStationsTests.FakeStations LastFm { get; } = new();
        public FakeCatalog Catalog { get; } = new();
        public bool Connected { get; set; } = true;
        public string? User { get; set; } = "DemoListener";
        public string? LastFmUser { get; set; }
        public HomeStations Stations { get; }
        public HomePageViewModel Home { get; }

        public Setup(ListenBrainzCreatedForTests test, bool enabled = true, bool lastFm = false)
        {
            LastFmUser = lastFm ? "listener" : null;
            Stations = new HomeStations(LastFm, new StationResolver(Search), () => LastFmUser,
                clock: test._time,
                listenBrainz: Source, listenBrainzConnected: () => Connected,
                createdFor: Source, listenBrainzUser: () => User, createdForStore: new CreatedForStore(test._dir))
            {
                Enabled = lastFm,
                CreatedForEnabled = enabled,
            };
            Home = new HomePageViewModel(Catalog, null!, Stations, (_, _) => Task.CompletedTask);
        }

        public async Task LoadAsync(params string[] shelves)
        {
            var load = Home.RefreshCommand.ExecuteAsync(null);
            Catalog.HomeCalls[^1].Complete(null, shelves);
            await load;
            await Home.CreatedForRefresh;
            await WhenStationsLoadedAsync();
        }

        public async Task WhenStationsLoadedAsync()
        {
            foreach (var shelf in Home.StationShelves)
                while (shelf.IsLoading)
                    await Task.Delay(5);
        }

        public string[] Layout() => Home.Items.Select(i => i switch
        {
            Shelf s => s.Title,
            StationShelfViewModel st => st.Playlist is { } p ? $"{p.Kind}" : $"{st.Kind}",
            _ => "?",
        }).ToArray();

        public StationShelfViewModel Shelf(CreatedForKind kind) =>
            Home.StationShelves.Single(s => s.Playlist?.Kind == kind);
    }

    private static IEnumerable<string> Ids(IEnumerable<CreatedForPlaylist> playlists) => playlists.Select(p => p.Id);

    // ---- HomeStations ----

    [Fact]
    public void The_user_asked_about_needs_the_setting_the_connection_and_a_name()
    {
        var setup = new Setup(this, enabled: false);
        Assert.Null(setup.Stations.CreatedForUser);

        setup.Stations.CreatedForEnabled = true;
        Assert.Equal("DemoListener", setup.Stations.CreatedForUser);

        setup.Connected = false;
        Assert.Null(setup.Stations.CreatedForUser);

        setup.Connected = true;
        setup.User = "";
        Assert.Null(setup.Stations.CreatedForUser);
        Assert.Empty(setup.Stations.CreatedForShown);
    }

    [Fact]
    public void Without_a_source_there_is_no_user()
    {
        var stations = new HomeStations(new HomeStationsTests.FakeStations(), new StationResolver(Search), () => null,
            listenBrainz: new FakeListenBrainz(), listenBrainzConnected: () => true,
            listenBrainzUser: () => "DemoListener")
        {
            CreatedForEnabled = true,
        };
        Assert.Null(stations.CreatedForUser);
    }

    [Fact]
    public void The_setting_raises_Changed_only_when_it_changes()
    {
        var setup = new Setup(this);
        var changed = 0;
        setup.Stations.Changed += () => changed++;

        setup.Stations.CreatedForEnabled = true;
        Assert.Equal(0, changed);
        setup.Stations.CreatedForEnabled = false;
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task A_fresh_saved_list_is_not_fetched_again_until_it_is_old_or_fresh_is_asked()
    {
        var setup = new Setup(this);

        await setup.Stations.RefreshCreatedForAsync(fresh: false, default);
        await setup.Stations.RefreshCreatedForAsync(fresh: false, default);
        Assert.Equal(["DemoListener"], setup.Source.ListCalls);

        // After a restart too.
        var restarted = new Setup(this);
        await restarted.Stations.RefreshCreatedForAsync(fresh: false, default);
        Assert.Empty(restarted.Source.ListCalls);
        Assert.Equal(["daily-1", "weekly-1"], Ids(restarted.Stations.CreatedForShown));

        _time.Now += HomeStations.Freshness + TimeSpan.FromMinutes(1);
        await setup.Stations.RefreshCreatedForAsync(fresh: false, default);
        Assert.Equal(2, setup.Source.ListCalls.Count);

        await setup.Stations.RefreshCreatedForAsync(fresh: true, default);
        Assert.Equal(3, setup.Source.ListCalls.Count);
    }

    [Fact]
    public async Task Only_the_newest_unexpired_playlist_of_each_kind_shows_in_Homes_order()
    {
        var setup = new Setup(this);
        await setup.Stations.RefreshCreatedForAsync(fresh: false, default);

        Assert.Equal(["daily-1", "weekly-1"], Ids(setup.Stations.CreatedForShown));

        // A day later the daily one has expired.
        _time.Now += TimeSpan.FromDays(1);
        Assert.Equal(["weekly-1"], Ids(setup.Stations.CreatedForShown));
    }

    [Fact]
    public async Task Changed_is_raised_only_when_the_shown_playlists_change()
    {
        var setup = new Setup(this);
        var changed = 0;
        setup.Stations.Changed += () => changed++;

        await setup.Stations.RefreshCreatedForAsync(fresh: false, default);
        Assert.Equal(1, changed);

        await setup.Stations.RefreshCreatedForAsync(fresh: true, default);
        Assert.Equal(1, changed);

        var next = Daily with { Id = "daily-2" };
        setup.Source.List = _ => [Weekly, next];
        await setup.Stations.RefreshCreatedForAsync(fresh: true, default);
        Assert.Equal(2, changed);
        Assert.Equal(["daily-2", "weekly-1"], Ids(setup.Stations.CreatedForShown));
    }

    [Fact]
    public async Task An_unknown_user_has_none_and_is_not_asked_again_while_fresh()
    {
        var setup = new Setup(this);
        await setup.Stations.RefreshCreatedForAsync(fresh: false, default);
        var changed = 0;
        setup.Stations.Changed += () => changed++;

        setup.Source.List = _ => throw new StationUnavailableException("HTTP 404");
        await setup.Stations.RefreshCreatedForAsync(fresh: true, default);

        Assert.Empty(setup.Stations.CreatedForShown);
        Assert.Equal(1, changed);
        await setup.Stations.RefreshCreatedForAsync(fresh: false, default);
        Assert.Equal(2, setup.Source.ListCalls.Count);
    }

    [Fact]
    public async Task A_network_failure_keeps_the_saved_list_and_does_not_throw()
    {
        var setup = new Setup(this);
        await setup.Stations.RefreshCreatedForAsync(fresh: false, default);
        var changed = 0;
        setup.Stations.Changed += () => changed++;

        setup.Source.List = _ => throw new HttpRequestException("No such host is known.");
        _time.Now += HomeStations.Freshness + TimeSpan.FromMinutes(1);
        await setup.Stations.RefreshCreatedForAsync(fresh: false, default);

        Assert.Equal(["daily-1", "weekly-1"], Ids(setup.Stations.CreatedForShown));
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task After_a_failure_only_Homes_refresh_asks_again_until_the_pause_ends()
    {
        // The scrobbler raises Changed often, and each one asks for a refresh.
        var setup = new Setup(this);
        setup.Source.List = _ => throw new HttpRequestException("No such host is known.");
        await setup.Stations.RefreshCreatedForAsync(fresh: false, default);
        Assert.Single(setup.Source.ListCalls);

        _time.Now += HomeStations.CreatedForRetryAfter - TimeSpan.FromSeconds(1);
        await setup.Stations.RefreshCreatedForAsync(fresh: false, default);
        Assert.Single(setup.Source.ListCalls);

        await setup.Stations.RefreshCreatedForAsync(fresh: true, default);
        Assert.Equal(2, setup.Source.ListCalls.Count);

        _time.Now += HomeStations.CreatedForRetryAfter;
        await setup.Stations.RefreshCreatedForAsync(fresh: false, default);
        Assert.Equal(3, setup.Source.ListCalls.Count);
    }

    [Fact]
    public async Task A_playlist_resolves_through_the_searches_and_is_saved_under_its_id()
    {
        var setup = new Setup(this);
        await setup.Stations.RefreshCreatedForAsync(fresh: false, default);

        var loaded = await setup.Stations.LoadCreatedForAsync(Daily, default);

        Assert.Equal(["ytm-crush", null], loaded.Rows.Select(r => r.Track?.VideoId));
        Assert.Equal(_time.Now, loaded.LoadedAt);
        Assert.Equal(["daily-1"], setup.Source.PlaylistCalls);

        var restarted = new Setup(this);
        var saved = Assert.IsType<CreatedForSnapshot>(restarted.Stations.SavedCreatedFor("daily-1"));
        Assert.Equal(["ytm-crush", null], saved.Rows.Select(r => r.Track?.VideoId));
        Assert.Equal(_time.Now, saved.LoadedAt);
        Assert.Null(restarted.Stations.SavedCreatedFor("weekly-1"));
    }

    [Fact]
    public async Task Another_users_saved_playlists_are_not_shown()
    {
        var alice = new Setup(this) { User = "alice" };
        await alice.Stations.RefreshCreatedForAsync(fresh: false, default);
        await alice.Stations.LoadCreatedForAsync(Daily, default);

        var bob = new Setup(this) { User = "bob" };
        Assert.Empty(bob.Stations.CreatedForShown);
        Assert.Null(bob.Stations.SavedCreatedFor("daily-1"));

        await bob.Stations.RefreshCreatedForAsync(fresh: false, default);
        Assert.Equal(["bob"], bob.Source.ListCalls);
    }

    // ---- CreatedForStore ----

    private static readonly StationRow[] Rows =
    [
        new(new StationTrack("Daft Punk", "Instant Crush", TimeSpan.FromSeconds(337)), StationMatch.Song,
            new TrackInfo { VideoId = "ytm1", Title = "Instant Crush", Artists = [new ArtistRef("Daft Punk", "UC1")] }),
        new(new StationTrack("Nobody", "Nothing"), StationMatch.NotFound, null),
    ];

    [Fact]
    public void The_store_reads_back_what_it_saved()
    {
        var store = new CreatedForStore(_dir);
        var listedAt = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var loadedAt = listedAt.AddMinutes(5);
        var noExpiry = new CreatedForPlaylist(CreatedForKind.WeeklyExploration, "exploration-1", listedAt, null);
        store.Save("DemoListener", new CreatedForSaved(listedAt, [Daily, Weekly, noExpiry],
            new Dictionary<string, CreatedForSnapshot> { ["daily-1"] = new("daily-1", loadedAt, Rows) }));

        var saved = Assert.IsType<CreatedForSaved>(store.Load("demolistener"));
        Assert.Equal(listedAt, saved.ListedAt);
        Assert.Equal([Daily, Weekly, noExpiry], saved.Playlists);
        var list = saved.Loaded["daily-1"];
        Assert.Equal(loadedAt, list.LoadedAt);
        Assert.Equal([StationMatch.Song, StationMatch.NotFound], list.Rows.Select(r => r.Match));
        Assert.Equal("UC1", list.Rows[0].Track!.Artists[0].BrowseId);
        var json = File.ReadAllText(store.Path);
        Assert.Contains("\"version\":1", json);
        Assert.Contains("\"dailyJams\"", json);
        Assert.Contains("\"weeklyJams\"", json);
        Assert.Contains("\"weeklyExploration\"", json);
        Assert.EndsWith("listenbrainz-stations.json", store.Path);
    }

    [Fact]
    public void The_lists_of_playlists_no_longer_listed_are_dropped()
    {
        var store = new CreatedForStore(_dir);
        var at = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        store.Save("DemoListener", new CreatedForSaved(at, [Weekly],
            new Dictionary<string, CreatedForSnapshot>
            {
                ["daily-1"] = new("daily-1", at, Rows),
                ["weekly-1"] = new("weekly-1", at, Rows),
            }));

        Assert.Equal(["weekly-1"], store.Load("DemoListener")!.Loaded.Keys);
    }

    [Fact]
    public void Another_users_file_is_nothing_saved()
    {
        var store = new CreatedForStore(_dir);
        store.Save("alice", new CreatedForSaved(DateTimeOffset.UtcNow, [Daily], new Dictionary<string, CreatedForSnapshot>()));
        Assert.Null(store.Load("bob"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "version": 2, "user": "DemoListener", "playlists": [] }""")]
    [InlineData("""{ "version": 0, "user": "DemoListener", "playlists": [] }""")]
    public void A_corrupt_or_newer_file_is_nothing_saved(string json)
    {
        Directory.CreateDirectory(_dir);
        var store = new CreatedForStore(_dir);
        File.WriteAllText(store.Path, json);
        Assert.Null(store.Load("DemoListener"));
    }

    [Fact]
    public void An_unknown_kind_is_skipped()
    {
        Directory.CreateDirectory(_dir);
        var store = new CreatedForStore(_dir);
        File.WriteAllText(store.Path, """
            { "version": 1, "user": "DemoListener", "listedAt": "2026-11-20T09:00:00+00:00",
              "playlists": [ { "kind": "topDiscoveries", "id": "x", "createdAt": "2026-11-20T00:00:00+00:00" },
                             { "kind": "dailyJams", "id": "daily-1", "createdAt": "2026-11-20T00:00:00+00:00" } ] }
            """);
        Assert.Equal(["daily-1"], Ids(store.Load("DemoListener")!.Playlists));
    }

    // ---- The shelf ----

    [Theory]
    [InlineData(CreatedForKind.DailyJams, "home.station.listenBrainz.dailyJams")]
    [InlineData(CreatedForKind.WeeklyJams, "home.station.listenBrainz.weeklyJams")]
    [InlineData(CreatedForKind.WeeklyExploration, "home.station.listenBrainz.weeklyExploration")]
    public void Each_kind_has_its_title(CreatedForKind kind, string key)
    {
        var setup = new Setup(this);
        var shelf = new StationShelfViewModel(Daily with { Kind = kind }, setup.Stations, (_, _) => Task.CompletedTask);

        Assert.Equal(Strings.Get(key), shelf.Title);
        Assert.Equal(StationService.ListenBrainz, shelf.Service);
        Assert.Null(shelf.Kind);
        Assert.Null(shelf.Artist);
        Assert.Equal(Strings.Get("station.listenBrainz.createdFor.refreshTip"), shelf.RefreshTip);
    }

    [Fact]
    public async Task A_saved_playlist_shows_without_loading_again()
    {
        var setup = new Setup(this);
        await setup.Stations.RefreshCreatedForAsync(fresh: false, default);
        await setup.Stations.LoadCreatedForAsync(Daily, default);
        // A playlist's tracks never change, so its age doesn't matter.
        _time.Now += HomeStations.Freshness + TimeSpan.FromMinutes(1);

        var restarted = new Setup(this);
        var shelf = new StationShelfViewModel(Daily, restarted.Stations, (_, _) => Task.CompletedTask);
        await shelf.ShowAsync();

        Assert.Empty(restarted.Source.PlaylistCalls);
        Assert.Equal(["ytm-crush"], shelf.Shelf!.Entries.Cast<SongEntry>().Select(e => e.Track.VideoId));
        Assert.Equal(Strings.Get("home.station.listenBrainz.dailyJams"), shelf.Shelf.Title);
    }

    [Fact]
    public async Task A_playlist_ListenBrainz_no_longer_has_says_so()
    {
        var setup = new Setup(this);
        setup.Source.Playlist = _ => throw new StationUnavailableException("HTTP 404");
        var shelf = new StationShelfViewModel(Daily, setup.Stations, (_, _) => Task.CompletedTask);

        await shelf.ShowAsync();

        Assert.Equal(Strings.Get("station.listenBrainz.createdFor.unavailable"), shelf.Unavailable);
        Assert.Null(shelf.Shelf);
        Assert.Null(shelf.LoadError);
    }

    // ---- Home ----

    [Fact]
    public async Task Home_shows_the_current_playlists_after_Last_fms_stations()
    {
        var setup = new Setup(this, lastFm: true);
        await setup.LoadAsync("A", "B", "C");

        Assert.Equal(["A", "B", "Mix", "Recommended", "DailyJams", "WeeklyJams", "C"], setup.Layout());
        Assert.Equal(["daily-1", "weekly-1"], setup.Source.PlaylistCalls.Order());
        Assert.NotNull(setup.Shelf(CreatedForKind.DailyJams).Shelf);
    }

    [Fact]
    public async Task Without_Last_fm_the_playlists_take_the_slot_alone()
    {
        var setup = new Setup(this);
        await setup.LoadAsync("A", "B", "C");

        Assert.Equal(["A", "B", "DailyJams", "WeeklyJams", "C"], setup.Layout());
        Assert.Empty(setup.LastFm.Calls);
    }

    [Fact]
    public async Task The_shelves_come_when_the_list_arrives()
    {
        var setup = new Setup(this);
        var gate = setup.Source.Gate = new TaskCompletionSource();
        // YouTube Music's shelves don't wait for ListenBrainz.
        var load = setup.Home.RefreshCommand.ExecuteAsync(null);
        setup.Catalog.HomeCalls[^1].Complete(null, "A", "B", "C");
        await load;
        Assert.Equal(["A", "B", "C"], setup.Layout());

        gate.SetResult();
        await setup.Home.CreatedForRefresh;
        await setup.WhenStationsLoadedAsync();

        Assert.Equal(["A", "B", "DailyJams", "WeeklyJams", "C"], setup.Layout());
    }

    [Fact]
    public async Task A_shelf_stays_while_its_playlist_does_and_is_replaced_by_a_new_one()
    {
        var setup = new Setup(this);
        await setup.LoadAsync("A", "B", "C");
        var daily = setup.Shelf(CreatedForKind.DailyJams);
        var weekly = setup.Shelf(CreatedForKind.WeeklyJams);

        setup.Source.List = _ => [Weekly, Daily with { Id = "daily-2" }];
        await setup.LoadAsync("A", "B", "C");

        Assert.NotSame(daily, setup.Shelf(CreatedForKind.DailyJams));
        Assert.Equal("daily-2", setup.Shelf(CreatedForKind.DailyJams).Playlist!.Id);
        Assert.Same(weekly, setup.Shelf(CreatedForKind.WeeklyJams));
        Assert.Equal(["A", "B", "DailyJams", "WeeklyJams", "C"], setup.Layout());
    }

    [Fact]
    public async Task Off_there_are_none_and_ListenBrainz_is_not_asked()
    {
        var setup = new Setup(this, enabled: false);
        await setup.LoadAsync("A", "B", "C");

        Assert.Equal(["A", "B", "C"], setup.Layout());
        Assert.Empty(setup.Source.ListCalls);
    }

    [Fact]
    public async Task Turning_it_on_while_Home_shows_adds_them_and_off_removes_them()
    {
        var setup = new Setup(this, enabled: false);
        await setup.LoadAsync("A", "B", "C");

        setup.Stations.CreatedForEnabled = true;
        await setup.Home.CreatedForRefresh;
        await setup.WhenStationsLoadedAsync();
        Assert.Equal(["A", "B", "DailyJams", "WeeklyJams", "C"], setup.Layout());

        setup.Stations.CreatedForEnabled = false;
        Assert.Equal(["A", "B", "C"], setup.Layout());
    }

    [Fact]
    public async Task A_kind_without_a_current_playlist_has_no_shelf()
    {
        var setup = new Setup(this);
        setup.Source.List = _ => [OldExploration];
        await setup.LoadAsync("A", "B", "C");

        Assert.Equal(["A", "B", "C"], setup.Layout());
        Assert.Empty(setup.Source.PlaylistCalls);
    }

    // ---- Settings ----

    private ListenBrainzStationsAccount Account() => new(
        ListenBrainzTokenVault.ForStations(_dir),
        () => new ListenBrainzClient(ListenBrainzClient.DefaultServer, "1.0",
            new HttpClient(new ListenBrainzArtistStationTests.ValidateStub())));

    [Fact]
    public async Task The_setting_is_off_by_default_persists_and_turns_the_shelves_on()
    {
        var app = new AppSettings();
        Assert.False(app.ListenBrainzCreatedForOnHome);
        var setup = new Setup(this, enabled: false);
        var saves = 0;
        var account = Account();
        await account.ConnectAsync("good-token");
        var settings = new ListenBrainzStationsSettingsViewModel(account, app, () => saves++, setup.Stations);
        Assert.False(settings.CreatedForOnHome);

        settings.CreatedForOnHome = true;

        Assert.True(app.ListenBrainzCreatedForOnHome);
        Assert.True(setup.Stations.CreatedForEnabled);
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task The_toggle_is_enabled_only_while_the_stations_token_is_connected()
    {
        var connected = Account();
        await connected.ConnectAsync("good-token");

        Assert.False(ToggleEnabled(Account()));
        Assert.True(ToggleEnabled(connected));
    }

    private bool ToggleEnabled(ListenBrainzStationsAccount account)
    {
        var enabled = false;
        Headless.Run(() =>
        {
            ShellResources.Ensure();
            using var scrobbler = new ListenBrainzScrobbler(
                server => new ListenBrainzClient(server, "1.0", new HttpClient(new ListenBrainzArtistStationTests.ValidateStub())),
                new ScrobbleQueue(_dir, "listenbrainz-queue.json", "listenbrainz"),
                new ListenBrainzTokenVault(_dir));
            var app = new AppSettings();
            var page = new SettingsPageViewModel(
                new Rambutan.Core.Settings.PlaybackSettings(), app, new AppearanceSettings(),
                new Rambutan.Integrations.UpdateService(null), new SessionViewModel(), () => { })
            {
                ListenBrainz = new ListenBrainzSettingsViewModel(scrobbler, app, null, account),
            };
            var view = new SettingsPage { DataContext = page };
            var window = new Window { Width = 900, Height = 1400, Content = view };
            window.Show();
            window.UpdateLayout();

            var toggle = view.GetVisualDescendants().OfType<ToggleSwitch>()
                .Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == Strings.Get("settings.listenbrainz.createdFor"));
            enabled = toggle.IsEffectivelyEnabled;
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == Strings.Get("settings.listenbrainz.createdFor.disclosure") && t.IsEffectivelyVisible);
            window.Close();
        });
        return enabled;
    }
}
