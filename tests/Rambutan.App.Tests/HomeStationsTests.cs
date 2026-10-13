using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Stations;

namespace Rambutan.App.Tests;

/// <summary>
/// Home's Last.fm station shelves: placed after YouTube Music's first
/// shelves while the setting is on and an account is connected, each with a
/// state of its own, and never in the way of the YTM shelves.
/// </summary>
public class HomeStationsTests
{
    internal sealed class FakeStations : IStationSource
    {
        public Func<StationKind, IReadOnlyList<StationTrack>> Answer { get; set; } = _ =>
        [
            new("Daft Punk", "Instant Crush", VideoId: "lf-crush"),
            new("Holy Ghost!", "Okay", VideoId: "lf-okay"),
            new("Nobody", "Nothing"),
        ];

        public List<(string User, StationKind Kind)> Calls { get; } = [];

        public Task<IReadOnlyList<StationTrack>> GetStationAsync(string user, StationKind kind, CancellationToken ct = default)
        {
            Calls.Add((user, kind));
            return Task.FromResult(Answer(kind));
        }

        public Func<string, IReadOnlyList<StationTrack>> ArtistAnswer { get; set; } = _ =>
        [
            new("Daft Punk", "Instant Crush", VideoId: "lf-crush"),
            new("Mr. Oizo", "Positif"),
        ];

        public List<string> ArtistCalls { get; } = [];

        public Task<IReadOnlyList<StationTrack>> GetArtistStationAsync(string artist, CancellationToken ct = default)
        {
            ArtistCalls.Add(artist);
            return Task.FromResult(ArtistAnswer(artist));
        }
    }

    private static Task<IReadOnlyList<TrackInfo>> Search(string query, CancellationToken _) =>
        Task.FromResult<IReadOnlyList<TrackInfo>>(query.StartsWith("Daft Punk", StringComparison.Ordinal)
            ? [new TrackInfo { VideoId = "ytm-crush", Title = "Instant Crush", Artists = [new ArtistRef("Daft Punk")] }]
            : []);

    internal sealed class Setup
    {
        public FakeCatalog Catalog { get; } = new();
        public FakeStations Source { get; } = new();
        public string? User { get; set; } = "listener";
        public HomeStations Stations { get; }
        public HomePageViewModel Home { get; }
        public List<(IReadOnlyList<TrackInfo> Tracks, int Start)> Played { get; } = [];

        public Setup(bool enabled = true)
        {
            Stations = new HomeStations(Source, new StationResolver(Search), () => User) { Enabled = enabled };
            Home = new HomePageViewModel(Catalog, null!, Stations, (tracks, start) =>
            {
                Played.Add((tracks, start));
                return Task.CompletedTask;
            });
        }

        public async Task LoadAsync(params string[] shelves)
        {
            var load = Home.RefreshCommand.ExecuteAsync(null);
            Catalog.HomeCalls[^1].Complete(null, shelves);
            await load;
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
            StationShelfViewModel st => $"{st.Kind}",
            _ => "?",
        }).ToArray();
    }

    [Fact]
    public async Task Stations_go_after_the_first_two_shelves()
    {
        var setup = new Setup();
        await setup.LoadAsync("Listen again", "Quick picks", "New releases");

        Assert.Equal(["Listen again", "Quick picks", "Mix", "Recommended", "New releases"], setup.Layout());
        Assert.Equal([("listener", StationKind.Mix), ("listener", StationKind.Recommended)], setup.Source.Calls);
        // YTM's own list is untouched.
        Assert.Equal(["Listen again", "Quick picks", "New releases"], setup.Home.Shelves.Select(s => s.Title));
    }

    [Fact]
    public async Task Off_by_default_and_without_an_account_there_are_none()
    {
        var off = new Setup(enabled: false);
        await off.LoadAsync("A", "B", "C");
        Assert.Equal(["A", "B", "C"], off.Layout());

        var signedOut = new Setup { User = null };
        await signedOut.LoadAsync("A", "B", "C");
        Assert.Equal(["A", "B", "C"], signedOut.Layout());
        Assert.Empty(signedOut.Source.Calls);
    }

    [Fact]
    public async Task With_few_shelves_the_stations_follow_them_and_move_up_as_more_arrive()
    {
        var setup = new Setup();
        var load = setup.Home.RefreshCommand.ExecuteAsync(null);
        setup.Catalog.HomeCalls[0].Complete("p2", "Listen again");
        await load;
        Assert.Equal(["Listen again", "Mix", "Recommended"], setup.Layout());

        var more = setup.Home.LoadMoreAsync();
        setup.Catalog.HomeCalls[1].Complete(null, "Quick picks", "New releases");
        await more;

        Assert.Equal(["Listen again", "Quick picks", "Mix", "Recommended", "New releases"], setup.Layout());
        Assert.Equal(2, setup.Source.Calls.Count);
    }

    [Fact]
    public async Task A_shelf_shows_what_plays_and_leaves_out_what_does_not()
    {
        var setup = new Setup();
        await setup.LoadAsync("A", "B");

        var mix = setup.Home.StationShelves[0];
        var shelf = Assert.IsType<Shelf>(mix.Shelf);
        Assert.Equal(["ytm-crush", "lf-okay"], shelf.Entries.Cast<SongEntry>().Select(e => e.Track.VideoId));
        Assert.Equal("Holy Ghost!", shelf.Entries[1].Subtitle);
        Assert.Equal(3, mix.Rows.Count);
        Assert.False(mix.IsEmpty);
        Assert.Null(mix.Unavailable);
        Assert.Null(mix.LoadError);
    }

    [Fact]
    public async Task A_card_plays_the_station_from_that_track()
    {
        var setup = new Setup();
        await setup.LoadAsync("A", "B");
        var mix = setup.Home.StationShelves[0];

        await mix.PlayFromCommand.ExecuteAsync(mix.Shelf!.Entries[1]);

        var (tracks, start) = Assert.Single(setup.Played);
        Assert.Equal(["ytm-crush", "lf-okay"], tracks.Select(t => t.VideoId));
        Assert.Equal(1, start);
    }

    [Fact]
    public async Task A_list_Last_fm_does_not_share_says_so()
    {
        var setup = new Setup();
        setup.Source.Answer = _ => throw new StationUnavailableException("HTTP 500");
        await setup.LoadAsync("A", "B");

        var mix = setup.Home.StationShelves[0];
        Assert.Null(mix.Shelf);
        Assert.Equal(Strings.Get("home.station.unavailable"), mix.Unavailable);
        Assert.Null(mix.LoadError);
        Assert.False(mix.IsLoading);
    }

    [Fact]
    public async Task An_empty_list_says_so()
    {
        var setup = new Setup();
        setup.Source.Answer = _ => [new StationTrack("Nobody", "Nothing")];
        await setup.LoadAsync("A", "B");

        Assert.True(setup.Home.StationShelves[0].IsEmpty);
    }

    [Fact]
    public async Task Offline_the_shelf_says_which_services_and_offers_try_again()
    {
        var setup = new Setup();
        setup.Source.Answer = _ => throw new HttpRequestException("No such host is known.");
        await setup.LoadAsync("A", "B");

        var mix = setup.Home.StationShelves[0];
        var error = Assert.IsType<LoadError>(mix.LoadError);
        Assert.Equal(Strings.Get("home.station.offline"), error.Title);
        Assert.NotNull(error.Retry);

        setup.Source.Answer = new FakeStations().Answer;
        await mix.LoadCommand.ExecuteAsync(null);
        Assert.Null(mix.LoadError);
        Assert.NotNull(mix.Shelf);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_cards_it_had()
    {
        var setup = new Setup();
        await setup.LoadAsync("A", "B");
        var mix = setup.Home.StationShelves[0];
        var before = mix.Shelf;

        setup.Source.Answer = _ => throw new HttpRequestException("reset");
        await mix.LoadCommand.ExecuteAsync(null);

        Assert.Same(before, mix.Shelf);
        Assert.NotNull(mix.LoadError);
    }

    [Fact]
    public async Task Turning_the_setting_off_and_on_removes_and_restores_them()
    {
        var setup = new Setup();
        await setup.LoadAsync("A", "B", "C");

        setup.Stations.Enabled = false;
        Assert.Equal(["A", "B", "C"], setup.Layout());
        Assert.Empty(setup.Home.StationShelves);

        setup.Stations.Enabled = true;
        await setup.WhenStationsLoadedAsync();
        Assert.Equal(["A", "B", "Mix", "Recommended", "C"], setup.Layout());
        // The lists loaded before come back without asking Last.fm again.
        Assert.Equal(2, setup.Source.Calls.Count);
        Assert.NotNull(setup.Home.StationShelves[0].Shelf);
    }

    [Fact]
    public async Task Disconnecting_Last_fm_removes_them_and_other_changes_move_nothing()
    {
        var setup = new Setup();
        await setup.LoadAsync("A", "B", "C");
        var items = setup.Home.Items.ToList();

        setup.Stations.ConnectionChanged();
        Assert.Equal(items, setup.Home.Items);

        setup.User = null;
        setup.Stations.ConnectionChanged();
        Assert.Equal(["A", "B", "C"], setup.Layout());
    }

    [Fact]
    public async Task Home_refresh_asks_for_a_fresh_sample()
    {
        var setup = new Setup();
        await setup.LoadAsync("A", "B");
        var mix = setup.Home.StationShelves[0];

        await setup.LoadAsync("A", "B");

        Assert.Same(mix, setup.Home.StationShelves[0]);
        Assert.Equal(4, setup.Source.Calls.Count);
    }
}

/// <summary>A station's page, from its shelf's "See all": every row of
/// the shelf's own sample, those with nothing to play marked, never dropped.</summary>
public class StationPageTests
{
    [Fact]
    public async Task The_page_lists_every_row_and_marks_the_ones_that_do_not_play_YTM_songs()
    {
        var setup = new HomeStationsTests.Setup();
        await setup.LoadAsync("A", "B");
        var page = new StationPageViewModel(setup.Home.StationShelves[0]);

        var items = page.Station.Items;
        Assert.Equal(["Instant Crush", "Okay", "Nothing"], items.Select(i => i.Title));
        Assert.Equal([true, true, false], items.Select(i => i.IsPlayable));
        Assert.Null(items[0].Note);
        Assert.Equal(Strings.Get("station.row.video"), items[1].Note);
        Assert.Equal(Strings.Get("station.row.notFound"), items[2].Note);
        Assert.Contains(Strings.Get("station.row.notFound"), items[2].AccessibleName);
        Assert.Equal("Nobody", items[2].Artist);
        Assert.Contains("3", page.Station.Summary);
        Assert.Contains("1", page.Station.Summary);
    }

    [Fact]
    public async Task Play_all_shuffle_and_a_row_play_only_what_plays()
    {
        var setup = new HomeStationsTests.Setup();
        await setup.LoadAsync("A", "B");
        var station = setup.Home.StationShelves[0];

        await station.PlayAllCommand.ExecuteAsync(null);
        await station.ShuffleCommand.ExecuteAsync(null);
        await station.PlayRowCommand.ExecuteAsync(station.Items[1]);
        await station.PlayRowCommand.ExecuteAsync(station.Items[2]);

        Assert.Equal(3, setup.Played.Count);
        Assert.Equal(["ytm-crush", "lf-okay"], setup.Played[0].Tracks.Select(t => t.VideoId));
        Assert.Equal(0, setup.Played[0].Start);
        Assert.Equal(["lf-okay", "ytm-crush"], setup.Played[1].Tracks.Select(t => t.VideoId).Order());
        Assert.Equal(1, setup.Played[2].Start);
    }

    [Fact]
    public async Task Refreshing_the_page_refreshes_the_shelf()
    {
        var setup = new HomeStationsTests.Setup();
        await setup.LoadAsync("A", "B");
        var station = setup.Home.StationShelves[0];
        var page = new StationPageViewModel(station);
        setup.Source.Answer = _ => [new("Pulp", "Babies", VideoId: "lf-babies")];

        await page.Station.LoadCommand.ExecuteAsync(null);

        Assert.Equal(["Babies"], page.Station.Items.Select(i => i.Title));
        Assert.Equal(["lf-babies"], station.Shelf!.Entries.Cast<SongEntry>().Select(e => e.Track.VideoId));
    }

    [Fact]
    public void Nothing_to_play_disables_play_all_and_shuffle()
    {
        var station = new StationShelfViewModel(StationKind.Mix,
            new HomeStations(new HomeStationsTests.FakeStations(), new StationResolver((_, _) => Task.FromResult<IReadOnlyList<TrackInfo>>([])), () => "x"),
            (_, _) => Task.CompletedTask);
        Assert.False(station.PlayAllCommand.CanExecute(null));
        Assert.False(station.ShuffleCommand.CanExecute(null));
    }
}

/// <summary>A loaded station shelf follows a language change: the shelf control
/// shows the title its Shelf was built with.</summary>
[Collection(nameof(LanguageSwitch))]
public class StationShelfLanguageTests
{
    [Fact]
    public async Task A_loaded_shelf_takes_the_new_language()
    {
        var setup = new HomeStationsTests.Setup();
        await setup.LoadAsync("A", "B");
        var mix = setup.Home.StationShelves[0];

        Headless.Run(() =>
        {
            try
            {
                Strings.SetLanguage(Strings.PseudoLocale);
                Assert.Equal(Strings.Get("home.station.mix"), mix.Shelf!.Title);
                Assert.NotEqual("Your Last.fm mix", mix.Shelf.Title);
            }
            finally
            {
                Strings.SetLanguage("en");
            }
        });
        Assert.Equal(Strings.Get("home.station.mix"), mix.Shelf!.Title);
    }
}

public class HomeStationsSettingTests
{
    [Fact]
    public void The_setting_is_off_by_default_and_turns_the_stations_on()
    {
        var app = new AppSettings();
        Assert.False(app.LastFmStationsOnHome);

        var stations = new HomeStations(null!, null!, () => "listener");
        var saves = 0;
        var page = new SettingsPageViewModel(
            new Rambutan.Core.Settings.PlaybackSettings(), app, new AppearanceSettings(),
            new Rambutan.Integrations.UpdateService(null), new SessionViewModel(), () => saves++)
        {
            Stations = stations,
        };
        Assert.False(page.LastFmStationsOnHome);

        page.LastFmStationsOnHome = true;

        Assert.True(app.LastFmStationsOnHome);
        Assert.True(stations.Enabled);
        Assert.Equal("listener", stations.User);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void Without_an_account_the_note_says_why_it_can_not_be_turned_on()
    {
        var page = new SettingsPageViewModel(
            new Rambutan.Core.Settings.PlaybackSettings(), new AppSettings(), new AppearanceSettings(),
            new Rambutan.Integrations.UpdateService(null), new SessionViewModel(), () => { });

        Assert.False(page.LastFmConnected);
        Assert.StartsWith(Strings.Get("settings.lastfm.stations.needsAccount"), page.LastFmStationsNote);
        Assert.Contains(Strings.Get("settings.lastfm.stations.disclosure"), page.LastFmStationsNote);
    }
}
