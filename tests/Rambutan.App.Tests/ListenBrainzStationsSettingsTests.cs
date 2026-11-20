using Avalonia.Controls;
using Avalonia.VisualTree;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Integrations.ListenBrainz;
using Rambutan.Integrations.Scrobbling;

namespace Rambutan.App.Tests;

/// <summary>The stations' rows in the Settings page's ListenBrainz section: their
/// own token, apart from the scrobbling connection.</summary>
public sealed class ListenBrainzStationsSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-lb-stations-settings-" + Guid.NewGuid().ToString("N"));

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

    private ListenBrainzStationsAccount Account() => new(
        ListenBrainzTokenVault.ForStations(_dir),
        () => new ListenBrainzClient(ListenBrainzClient.DefaultServer, "1.0",
            new HttpClient(new ListenBrainzArtistStationTests.ValidateStub())));

    private ListenBrainzScrobbler Scrobbler() => new(
        server => new ListenBrainzClient(server, "1.0", new HttpClient(new ListenBrainzArtistStationTests.ValidateStub())),
        new ScrobbleQueue(_dir, "listenbrainz-queue.json", "listenbrainz"),
        new ListenBrainzTokenVault(_dir));

    [Fact]
    public void Without_an_account_the_section_has_no_station_rows()
    {
        using var scrobbler = Scrobbler();
        Assert.Null(new ListenBrainzSettingsViewModel(scrobbler, new AppSettings(), null).Stations);
    }

    [Fact]
    public void Not_connected_Connect_waits_for_a_token()
    {
        var stations = new ListenBrainzStationsSettingsViewModel(Account());

        Assert.Equal(Strings.Get("settings.listenbrainz.status.notConnected"), stations.Status);
        Assert.True(stations.ShowConnect);
        Assert.False(stations.Connected);
        Assert.False(stations.CanConnect);
        Assert.Equal("", stations.Notice);

        stations.Token = "  ";
        Assert.False(stations.CanConnect);
        stations.Token = "good-token";
        Assert.True(stations.CanConnect);
    }

    [Fact]
    public async Task Connect_keeps_the_token_clears_the_field_and_Disconnect_forgets_it()
    {
        var account = Account();
        var stations = new ListenBrainzStationsSettingsViewModel(account) { Token = "good-token" };

        await stations.ConnectCommand.ExecuteAsync(null);

        Assert.True(stations.Connected);
        Assert.False(stations.ShowConnect);
        Assert.False(stations.CanConnect);
        Assert.Equal(Strings.Format("settings.listenbrainz.status.connected", ("user", "DemoListener")), stations.Status);
        Assert.Equal("", stations.Token);
        Assert.Equal("good-token", account.Token);

        await stations.DisconnectCommand.ExecuteAsync(null);

        Assert.False(stations.Connected);
        Assert.True(stations.ShowConnect);
        Assert.Equal(Strings.Get("settings.listenbrainz.status.notConnected"), stations.Status);
        Assert.Null(ListenBrainzTokenVault.ForStations(_dir).Load());
    }

    [Fact]
    public async Task A_refused_token_says_so_and_stays_in_the_field()
    {
        var stations = new ListenBrainzStationsSettingsViewModel(Account()) { Token = "bad-token" };

        await stations.ConnectCommand.ExecuteAsync(null);

        Assert.False(stations.Connected);
        Assert.Equal(Strings.Get("settings.listenbrainz.notice.connectTokenInvalid"), stations.Notice);
        Assert.Equal("bad-token", stations.Token);
    }

    [Theory]
    [InlineData(ListenBrainzStationsNotice.TokenInvalid, "settings.listenbrainz.notice.connectTokenInvalid")]
    [InlineData(ListenBrainzStationsNotice.Unreachable, "settings.listenbrainz.stations.notice.unreachable")]
    [InlineData(ListenBrainzStationsNotice.RateLimited, "settings.listenbrainz.notice.connectRateLimited")]
    [InlineData(ListenBrainzStationsNotice.Refused, "settings.listenbrainz.notice.connectRefused")]
    [InlineData(ListenBrainzStationsNotice.TokenRefused, "settings.listenbrainz.stations.notice.tokenRefused")]
    public void Each_notice_has_its_words(ListenBrainzStationsNotice notice, string key) =>
        Assert.Equal(Strings.Get(key), ListenBrainzStationsSettingsViewModel.NoticeText(notice));

    [Fact]
    public async Task A_station_refused_with_the_saved_token_asks_for_a_new_one()
    {
        var account = Account();
        await account.ConnectAsync("good-token");
        var stations = new ListenBrainzStationsSettingsViewModel(account);

        account.TokenRefused();
        stations.Refresh();

        Assert.True(stations.Connected);
        Assert.Equal(Strings.Get("settings.listenbrainz.stations.notice.tokenRefused"), stations.Notice);
    }

    [Fact]
    public async Task The_token_box_shows_only_while_not_connected()
    {
        var connected = Account();
        await connected.ConnectAsync("good-token");

        Assert.True(TokenBoxShown(Account()));
        Assert.False(TokenBoxShown(connected));
    }

    private bool TokenBoxShown(ListenBrainzStationsAccount account)
    {
        var shown = false;
        Headless.Run(() =>
        {
            ShellResources.Ensure();
            using var scrobbler = Scrobbler();
            var page = new SettingsPageViewModel(
                new Rambutan.Core.Settings.PlaybackSettings(), new AppSettings(), new AppearanceSettings(),
                new Rambutan.Integrations.UpdateService(null), new SessionViewModel(), () => { })
            {
                ListenBrainz = new ListenBrainzSettingsViewModel(scrobbler, new AppSettings(), null, account),
            };
            var view = new SettingsPage { DataContext = page };
            var window = new Window { Width = 900, Height = 1400, Content = view };
            window.Show();
            window.UpdateLayout();

            var box = view.GetVisualDescendants().OfType<TextBox>()
                .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == Strings.Get("settings.listenbrainz.stations.token"));
            shown = box.IsEffectivelyVisible;
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == Strings.Get("settings.listenbrainz.stations.disclosure") && t.IsEffectivelyVisible);
            window.Close();
        });
        return shown;
    }
}
