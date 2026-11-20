using System.Net;
using System.Text;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;
using Rambutan.Core.Stations;
using Rambutan.Integrations.ListenBrainz;

namespace Rambutan.App.Tests;

/// <summary>
/// An artist's ListenBrainz station, beside the Last.fm one: there whenever the
/// stations' own ListenBrainz account is connected, whatever the Last.fm
/// setting, resolved through the same searches, and worded as ListenBrainz's.
/// </summary>
public sealed class ListenBrainzArtistStationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-lb-artist-" + Guid.NewGuid().ToString("N"));
    private int _searches;

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

    internal sealed class FakeArtistStations : IArtistStationSource
    {
        public Func<ArtistSeed, IReadOnlyList<StationTrack>> Answer { get; set; } = _ =>
        [
            new("Daft Punk", "Instant Crush"),
            new("Mr. Oizo", "Positif"),
        ];

        public List<ArtistSeed> Calls { get; } = [];

        public Task<IReadOnlyList<StationTrack>> GetArtistStationAsync(ArtistSeed artist, CancellationToken ct = default)
        {
            Calls.Add(artist);
            return Task.FromResult(Answer(artist));
        }
    }

    /// <summary>Answers validate-token: valid only for "good-token".</summary>
    internal sealed class ValidateStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = request.Headers.Authorization?.ToString() == "Token good-token"
                ? """{"code":200,"message":"Token valid.","valid":true,"user_name":"DemoListener"}"""
                : """{"code":200,"message":"Token invalid.","valid":false}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private ListenBrainzStationsAccount Account() => new(
        ListenBrainzTokenVault.ForStations(_dir),
        () => new ListenBrainzClient(ListenBrainzClient.DefaultServer, "1.0", new HttpClient(new ValidateStub())));

    private Task<IReadOnlyList<TrackInfo>> Search(string query, CancellationToken _)
    {
        Interlocked.Increment(ref _searches);
        return Task.FromResult<IReadOnlyList<TrackInfo>>(query.StartsWith("Daft Punk", StringComparison.Ordinal)
            ? [new TrackInfo { VideoId = "ytm-crush", Title = "Instant Crush", Artists = [new ArtistRef("Daft Punk")] }]
            : []);
    }

    private HomeStations Stations(
        FakeArtistStations? listenBrainz, Func<bool> connected, Action? refused = null,
        string? lastFmUser = null, HomeStationsTests.FakeStations? lastFm = null, StationMatchCache? cache = null) =>
        new(lastFm ?? new HomeStationsTests.FakeStations(), new StationResolver(Search, cache: cache), () => lastFmUser,
            listenBrainz: listenBrainz, listenBrainzConnected: connected, listenBrainzTokenRefused: refused)
        {
            Enabled = lastFmUser is not null,
        };

    private static ArtistSeed Seed(string name = "Daft Punk") => new(name, []);

    private static async Task LoadedAsync(StationShelfViewModel station)
    {
        while (station.IsLoading)
            await Task.Delay(5);
    }

    // ---- HomeStations ----

    [Fact]
    public async Task The_station_loads_only_while_the_account_is_connected_whatever_Last_fm()
    {
        var source = new FakeArtistStations();
        var connected = false;
        var stations = Stations(source, () => connected);

        Assert.False(stations.ListenBrainzAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stations.LoadListenBrainzArtistAsync(Seed()));
        Assert.Empty(source.Calls);

        // No Last.fm account and the Last.fm setting off: still available.
        connected = true;
        Assert.Null(stations.User);
        Assert.True(stations.ListenBrainzAvailable);
        var (rows, _) = await stations.LoadListenBrainzArtistAsync(Seed());
        Assert.Equal(["ytm-crush", null], rows.Select(r => r.Track?.VideoId));

        // Without a source (a demo), never.
        Assert.False(Stations(null, () => true).ListenBrainzAvailable);
    }

    [Fact]
    public async Task It_resolves_through_the_searches_Last_fms_stations_share()
    {
        var cache = new StationMatchCache();
        var lastFm = new HomeStationsTests.FakeStations { ArtistAnswer = _ => [new("Daft Punk", "Instant Crush")] };
        var stations = Stations(new FakeArtistStations(), () => true, lastFmUser: "listener", lastFm: lastFm, cache: cache);

        await stations.LoadArtistAsync("Daft Punk");
        Assert.Equal(1, _searches);

        var (rows, _) = await stations.LoadListenBrainzArtistAsync(Seed());

        // Instant Crush was searched for Last.fm's station; only Positif is new.
        Assert.Equal(2, _searches);
        Assert.Equal(StationMatch.Song, rows[0].Match);
        Assert.Equal(StationMatch.NotFound, rows[1].Match);
    }

    [Fact]
    public async Task A_refused_token_tells_the_account_and_is_rethrown()
    {
        var account = Account();
        await account.ConnectAsync("good-token");
        var source = new FakeArtistStations { Answer = _ => throw new StationTokenRejectedException("HTTP 401") };
        var stations = Stations(source, () => account.Connected, account.TokenRefused);

        await Assert.ThrowsAsync<StationTokenRejectedException>(() => stations.LoadListenBrainzArtistAsync(Seed()));

        Assert.Equal(ListenBrainzStationsNotice.TokenRefused, account.Notice);
        Assert.True(account.Connected);
    }

    [Fact]
    public async Task No_station_for_the_artist_does_not_touch_the_account()
    {
        var refused = 0;
        var source = new FakeArtistStations { Answer = _ => throw new StationUnavailableException("no station") };
        var stations = Stations(source, () => true, () => refused++);

        await Assert.ThrowsAsync<StationUnavailableException>(() => stations.LoadListenBrainzArtistAsync(Seed()));

        Assert.Equal(0, refused);
    }

    // ---- The station's shelf and page ----

    private (StationShelfViewModel LastFm, StationShelfViewModel ListenBrainz) Both(
        FakeArtistStations source, HomeStationsTests.FakeStations? lastFm = null)
    {
        var stations = Stations(source, () => true, lastFmUser: "listener", lastFm: lastFm);
        return (
            new StationShelfViewModel("Daft Punk", stations, (_, _) => Task.CompletedTask),
            new StationShelfViewModel(Seed(), StationService.ListenBrainz, stations, (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task The_ListenBrainz_station_reads_differently_from_Last_fms()
    {
        var source = new FakeArtistStations();
        var (lastFm, listenBrainz) = Both(source);

        Assert.Equal(StationService.LastFm, lastFm.Service);
        Assert.Equal(StationService.ListenBrainz, listenBrainz.Service);
        Assert.Equal("Daft Punk", listenBrainz.Artist);
        Assert.Equal(Strings.Format("station.listenBrainz.title", ("name", "Daft Punk")), listenBrainz.Title);
        Assert.Equal(Strings.Format("station.artist.title", ("name", "Daft Punk")), lastFm.Title);
        Assert.NotEqual(lastFm.Title, listenBrainz.Title);
        Assert.Equal(Strings.Get("station.listenBrainz.refreshTip"), listenBrainz.RefreshTip);
        Assert.Equal(Strings.Get("station.page.refreshTip"), lastFm.RefreshTip);
        Assert.Equal(Strings.Get("station.listenBrainz.empty"), listenBrainz.EmptyText);
        Assert.Equal(Strings.Get("station.page.empty"), lastFm.EmptyText);
        Assert.Equal(Strings.Get("station.listenBrainz.loading"), listenBrainz.LoadingText);
        Assert.Equal(Strings.Get("home.station.loading"), lastFm.LoadingText);

        await lastFm.LoadAsync();
        await listenBrainz.LoadAsync();

        Assert.StartsWith(Strings.Format("station.listenBrainz.from", ("date", "")).Trim(), listenBrainz.LoadedText);
        Assert.StartsWith(Strings.Format("station.page.from", ("date", "")).Trim(), lastFm.LoadedText);
        Assert.NotEqual(lastFm.LoadedText, listenBrainz.LoadedText);
        Assert.Equal(Strings.Format("station.listenBrainz.title", ("name", "Daft Punk")), listenBrainz.Shelf!.Title);
        Assert.Equal(["Daft Punk"], source.Calls.Select(c => c.Name));
    }

    [Fact]
    public async Task No_station_says_ListenBrainz_has_none()
    {
        var source = new FakeArtistStations { Answer = _ => throw new StationUnavailableException("no station") };
        var (lastFm, listenBrainz) = Both(source, new HomeStationsTests.FakeStations
        {
            ArtistAnswer = _ => throw new StationUnavailableException("HTTP 404"),
        });

        await lastFm.LoadAsync();
        await listenBrainz.LoadAsync();

        Assert.Equal(Strings.Get("station.listenBrainz.unavailable"), listenBrainz.Unavailable);
        Assert.Equal(Strings.Get("station.artist.unavailable"), lastFm.Unavailable);
        Assert.Null(listenBrainz.LoadError);
    }

    [Fact]
    public async Task A_refused_token_says_to_connect_again()
    {
        var source = new FakeArtistStations { Answer = _ => throw new StationTokenRejectedException("HTTP 401") };
        var (_, listenBrainz) = Both(source);

        await listenBrainz.LoadAsync();

        Assert.Equal(Strings.Get("station.listenBrainz.tokenRefused"), listenBrainz.Unavailable);
        Assert.NotEqual(Strings.Get("station.listenBrainz.unavailable"), listenBrainz.Unavailable);
        Assert.Null(listenBrainz.LoadError);

        // A later answer clears it.
        source.Answer = new FakeArtistStations().Answer;
        await listenBrainz.LoadAsync();
        Assert.Null(listenBrainz.Unavailable);
    }

    [Fact]
    public async Task Offline_the_title_names_ListenBrainz()
    {
        var source = new FakeArtistStations { Answer = _ => throw new HttpRequestException("No such host is known.") };
        var (_, listenBrainz) = Both(source);

        await listenBrainz.LoadAsync();

        var error = Assert.IsType<LoadError>(listenBrainz.LoadError);
        Assert.Equal(Strings.Get("station.listenBrainz.offline"), error.Title);
    }

    // ---- The artist's page ----

    private sealed class PageSetup
    {
        public FakeCatalog Catalog { get; } = new();
        public FakeArtistStations Source { get; } = new();
        public HomeStationsTests.FakeStations LastFm { get; } = new();
        public NavigationService Navigation { get; }
        public ArtistPageViewModel Page { get; }

        public PageSetup(ListenBrainzArtistStationTests test, Func<bool> connected, params string[] topSongs)
        {
            Catalog.Artist = new ArtistPage
            {
                BrowseId = "UCdp", Name = "Daft Punk", Shelves = [],
                TopSongs = topSongs.Select(t => new TrackInfo { VideoId = "v-" + t, Title = t, Artists = [] }).ToList(),
            };
            var stations = test.Stations(Source, connected, lastFmUser: "listener", lastFm: LastFm);
            var coordinator = FakePlayback.Coordinator(new QueueModel());
            Navigation = new NavigationService(Catalog, coordinator, null!, stations: stations);
            Page = new ArtistPageViewModel(Catalog, coordinator, Navigation, "UCdp") { Stations = stations };
        }

        public StationShelfViewModel Opened => Assert.IsType<StationPageViewModel>(Navigation.Current).Station;
    }

    [Fact]
    public async Task The_page_offers_the_station_while_the_account_is_connected()
    {
        var account = Account();
        var setup = new PageSetup(this, () => account.Connected);
        await setup.Page.LoadAsync();
        Assert.False(setup.Page.CanOpenListenBrainzStation);
        Assert.False(setup.Page.OpenListenBrainzStationCommand.CanExecute(null));

        await account.ConnectAsync("good-token");
        var told = 0;
        setup.Page.PropertyChanged += (_, e) =>
            told += e.PropertyName == nameof(ArtistPageViewModel.CanOpenListenBrainzStation) ? 1 : 0;
        setup.Page.StationsChanged();
        Assert.Equal(1, told);
        Assert.True(setup.Page.CanOpenListenBrainzStation);
        Assert.True(setup.Page.OpenListenBrainzStationCommand.CanExecute(null));

        await account.DisconnectAsync();
        setup.Page.StationsChanged();
        Assert.False(setup.Page.CanOpenListenBrainzStation);
    }

    [Fact]
    public async Task The_page_needs_the_artists_name()
    {
        var setup = new PageSetup(this, () => true);
        Assert.False(setup.Page.CanOpenListenBrainzStation);
        await setup.Page.LoadAsync();
        Assert.True(setup.Page.CanOpenListenBrainzStation);
    }

    [Fact]
    public async Task The_seed_is_the_name_and_at_most_three_top_songs_and_the_station_is_kept()
    {
        var setup = new PageSetup(this, () => true, "One More Time", "Around the World", "Get Lucky", "Da Funk");
        await setup.Page.LoadAsync();

        setup.Page.OpenListenBrainzStationCommand.Execute(null);
        var station = setup.Opened;
        await LoadedAsync(station);

        var seed = Assert.Single(setup.Source.Calls);
        Assert.Equal("Daft Punk", seed.Name);
        Assert.Equal(["One More Time", "Around the World", "Get Lucky"], seed.SongTitles);
        Assert.Equal(StationService.ListenBrainz, station.Service);
        Assert.Empty(setup.LastFm.ArtistCalls);

        setup.Navigation.Back();
        setup.Page.OpenListenBrainzStationCommand.Execute(null);
        Assert.Same(station, setup.Opened);
        Assert.Single(setup.Source.Calls);

        // Last.fm's station is another one.
        setup.Navigation.Back();
        setup.Page.OpenStationCommand.Execute(null);
        Assert.NotSame(station, setup.Opened);
        Assert.Equal(StationService.LastFm, setup.Opened.Service);
    }

    [Fact]
    public async Task An_artist_without_top_songs_sends_the_name_alone()
    {
        var setup = new PageSetup(this, () => true);
        await setup.Page.LoadAsync();

        setup.Page.OpenListenBrainzStationCommand.Execute(null);
        await LoadedAsync(setup.Opened);

        Assert.Empty(Assert.Single(setup.Source.Calls).SongTitles);
    }

    // ---- An artist card's menu ----

    private static readonly ArtistEntry DaftPunk = new() { Title = "Daft Punk", BrowseId = "UCdp" };

    private CollectionActionsViewModel Cards(HomeStations stations, List<StationShelfViewModel> opened)
    {
        var catalog = new FakeCatalog();
        var queue = new QueueModel();
        var actions = new TrackActionsViewModel(catalog, queue, null!, new TrackRatings(catalog),
            new ToastHostViewModel(new ManualClock().Schedule));
        return new CollectionActionsViewModel(catalog, FakePlayback.Coordinator(queue), actions, stations, opened.Add);
    }

    [Fact]
    public async Task The_card_menu_offers_the_station_only_while_available_right_after_Last_fms()
    {
        var connected = false;
        var source = new FakeArtistStations();
        var opened = new List<StationShelfViewModel>();
        var cards = Cards(Stations(source, () => connected, lastFmUser: "listener"), opened);
        string?[] Headers() => CollectionMenuModel.Build(DaftPunk, cards).SelectMany(g => g).Select(e => e.Header).ToArray();

        Assert.False(cards.CanOpenListenBrainzStation);
        Assert.DoesNotContain(Strings.Get("artist.listenBrainzStation"), Headers());

        connected = true;
        Assert.True(cards.CanOpenListenBrainzStation);
        var headers = Headers();
        var lastFm = Array.IndexOf(headers, Strings.Get("artist.lastFmStation"));
        Assert.True(lastFm >= 0);
        Assert.Equal(Strings.Get("artist.listenBrainzStation"), headers[lastFm + 1]);

        var entry = CollectionMenuModel.Build(DaftPunk, cards).SelectMany(g => g)
            .Single(e => e.Header == Strings.Get("artist.listenBrainzStation"));
        entry.Command!.Execute(entry.Parameter);
        var station = Assert.Single(opened);
        Assert.Equal(StationService.ListenBrainz, station.Service);
        await station.LoadAsync();
        var seed = Assert.Single(source.Calls);
        Assert.Equal("Daft Punk", seed.Name);
        Assert.Empty(seed.SongTitles);
    }

    [Fact]
    public void The_card_menu_offers_it_without_Last_fm()
    {
        var cards = Cards(Stations(new FakeArtistStations(), () => true), []);
        string?[] headers = CollectionMenuModel.Build(DaftPunk, cards).SelectMany(g => g).Select(e => e.Header).ToArray();

        Assert.DoesNotContain(Strings.Get("artist.lastFmStation"), headers);
        Assert.Contains(Strings.Get("artist.listenBrainzStation"), headers);
    }
}
