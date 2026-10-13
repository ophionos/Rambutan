using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;
using Rambutan.Core.Stations;

namespace Rambutan.App.Tests;

/// <summary>
/// An artist's Last.fm station: an action on the artist's page, there
/// only while the stations setting is on and Last.fm is connected. The station
/// loads when the action is used, never with the page, and opens on the same
/// rows when it is opened again.
/// </summary>
public class ArtistStationTests
{
    private sealed class Setup
    {
        public FakeCatalog Catalog { get; } = new()
        {
            Artist = new ArtistPage { BrowseId = "UCdp", Name = "Daft Punk", TopSongs = [], Shelves = [] },
        };
        public HomeStationsTests.FakeStations Source { get; } = new();
        public string? User { get; set; } = "listener";
        public HomeStations Stations { get; }
        public NavigationService Navigation { get; }
        public ArtistPageViewModel Page { get; }

        public Setup()
        {
            Stations = new HomeStations(Source, new StationResolver(Search), () => User) { Enabled = true };
            var coordinator = FakePlayback.Coordinator(new QueueModel());
            Navigation = new NavigationService(Catalog, coordinator, null!, stations: Stations);
            Page = new ArtistPageViewModel(Catalog, coordinator, Navigation, "UCdp") { Stations = Stations };
        }

        public StationShelfViewModel Opened => Assert.IsType<StationPageViewModel>(Navigation.Current).Station;

        public async Task OpenAsync()
        {
            Page.OpenStationCommand.Execute(null);
            while (Opened.IsLoading)
                await Task.Delay(5);
        }
    }

    private static Task<IReadOnlyList<TrackInfo>> Search(string query, CancellationToken _) =>
        Task.FromResult<IReadOnlyList<TrackInfo>>(query.StartsWith("Daft Punk", StringComparison.Ordinal)
            ? [new TrackInfo { VideoId = "ytm-crush", Title = "Instant Crush", Artists = [new ArtistRef("Daft Punk")] }]
            : []);

    [Fact]
    public async Task The_action_is_there_only_while_stations_are_on_and_connected()
    {
        var setup = new Setup();
        Assert.False(setup.Page.CanOpenStation);    // no name until the page loads
        await setup.Page.LoadAsync();
        Assert.True(setup.Page.CanOpenStation);

        setup.Stations.Enabled = false;
        setup.Page.StationsChanged();
        Assert.False(setup.Page.CanOpenStation);
        Assert.False(setup.Page.OpenStationCommand.CanExecute(null));

        setup.Stations.Enabled = true;
        setup.User = null;    // Last.fm disconnected
        setup.Page.StationsChanged();
        Assert.False(setup.Page.CanOpenStation);

        var without = new ArtistPageViewModel(setup.Catalog, null!, setup.Navigation, "UCdp");
        await without.LoadAsync();
        Assert.False(without.CanOpenStation);
    }

    [Fact]
    public async Task The_station_loads_when_opened_and_opens_again_on_the_same_rows()
    {
        var setup = new Setup();
        await setup.Page.LoadAsync();
        Assert.Empty(setup.Source.ArtistCalls);

        await setup.OpenAsync();
        var station = setup.Opened;
        Assert.Equal(["Daft Punk"], setup.Source.ArtistCalls);
        Assert.Equal(Strings.Format("station.artist.title", ("name", "Daft Punk")), station.Title);
        Assert.Equal(["ytm-crush", null], station.Items.Select(i => i.Track?.VideoId));
        Assert.Empty(setup.Source.Calls);

        setup.Navigation.Back();
        await setup.OpenAsync();
        Assert.Same(station, setup.Opened);
        Assert.Single(setup.Source.ArtistCalls);
    }

    [Fact]
    public async Task An_artist_Last_fm_has_no_station_for_says_so()
    {
        var setup = new Setup();
        setup.Source.ArtistAnswer = _ => throw new StationUnavailableException("HTTP 404");
        await setup.Page.LoadAsync();

        await setup.OpenAsync();

        Assert.Equal(Strings.Get("station.artist.unavailable"), setup.Opened.Unavailable);
        Assert.False(setup.Opened.HasNoTracks);

        // Opening it again tries again.
        setup.Source.ArtistAnswer = _ => [];
        setup.Navigation.Back();
        await setup.OpenAsync();
        Assert.Equal(2, setup.Source.ArtistCalls.Count);
        Assert.Null(setup.Opened.Unavailable);
        Assert.True(setup.Opened.HasNoTracks);
    }

    [Fact]
    public void The_button_follows_the_setting_while_the_page_is_shown() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var setup = new Setup();
        setup.Page.LoadAsync().GetAwaiter().GetResult();
        var view = new Rambutan.App.Views.ArtistPage { DataContext = setup.Page };
        var window = new Window { Width = 1000, Height = 700, DataContext = new MainViewModel(), Content = view };
        window.Show();
        window.UpdateLayout();

        var button = view.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == Strings.Get("artist.lastFmStation"));
        Assert.True(button.IsEffectivelyVisible);

        setup.Stations.Enabled = false;
        window.UpdateLayout();
        Assert.False(button.IsEffectivelyVisible);

        // Off screen, the page doesn't listen; shown again, it catches up.
        window.Content = null;
        var told = 0;
        setup.Page.PropertyChanged += (_, e) => told += e.PropertyName == nameof(ArtistPageViewModel.CanOpenStation) ? 1 : 0;
        setup.Stations.Enabled = true;
        Assert.Equal(0, told);
        window.Content = view;
        window.UpdateLayout();
        Assert.True(button.IsEffectivelyVisible);
        window.Close();
    });
}
