using System.Net;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Integrations.LastFm;

namespace Rambutan.App.Tests;

/// <summary>The Settings page's Last.fm section and the session vault.</summary>
public sealed class LastFmSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-lastfm-app-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>Answers the auth calls in Last.fm API 2.0's documented shapes.</summary>
    private sealed class StubLastFm : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var json = body.Contains("method=auth.getToken") ? """{"token":"tok"}"""
                : body.Contains("method=auth.getSession") ? """{"session":{"subscriber":0,"name":"DemoListener","key":"SK"}}"""
                : """{"nowplaying":{}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private LastFmScrobbler Scrobbler(ILastFmSessionStore? sessions = null)
    {
        var scrobbler = new LastFmScrobbler(
            new LastFmClient("key", "secret", new HttpClient(new StubLastFm())),
            new ScrobbleQueue(_dir),
            sessions ?? new LastFmSessionVault(_dir));
        scrobbler.Start();
        return scrobbler;
    }

    private static SettingsPageViewModel Page(
        LastFmScrobbler? scrobbler, AppSettings? app = null, Action? persist = null,
        Func<string, Task<bool>>? openUrl = null) =>
        new(new Rambutan.Core.Settings.PlaybackSettings(), app ?? new AppSettings(), new AppearanceSettings(),
            new Rambutan.Integrations.UpdateService(null), new SessionViewModel(), persist ?? (() => { }),
            lastFm: scrobbler, openUrl: openUrl);

    [Fact]
    public void WithoutKeys_TheSectionSaysSo_AndOffersNothing()
    {
        var page = Page(null);

        Assert.False(page.LastFmAvailable);
        Assert.False(page.LastFmCanConnect);
        Assert.False(page.LastFmConnected);
        Assert.Equal(SettingsPageViewModel.LastFmUnavailableText, page.LastFmStatus);
    }

    [Fact]
    public async Task IsOffByDefault_NothingConnectedOnAFreshInstall()
    {
        using var scrobbler = Scrobbler();
        await scrobbler.WhenIdleAsync();

        var page = Page(scrobbler);

        Assert.True(page.LastFmCanConnect);
        Assert.Equal("Not connected", page.LastFmStatus);
        Assert.Equal(LastFmConnection.Disconnected, scrobbler.Connection);
    }

    [Fact]
    public async Task Connect_OpensTheApprovalPage_ThenFinishShowsTheAccount()
    {
        using var scrobbler = Scrobbler();
        string? opened = null;
        var page = Page(scrobbler, openUrl: url =>
        {
            opened = url;
            return Task.FromResult(true);
        });

        await page.ConnectLastFmCommand.ExecuteAsync(null);

        Assert.Equal("https://www.last.fm/api/auth/?api_key=key&token=tok", opened);
        Assert.True(page.LastFmAwaiting);
        Assert.Equal("Waiting for you to approve Rambutan in the browser…", page.LastFmStatus);

        await page.FinishLastFmCommand.ExecuteAsync(null);

        Assert.True(page.LastFmConnected);
        Assert.Equal("Connected as DemoListener", page.LastFmStatus);
        Assert.Equal("", page.LastFmAuthorizeUrl);

        // The key survives a restart, protected like the cookies.
        Assert.Equal(new LastFmSession("DemoListener", "SK"), new LastFmSessionVault(_dir).Load());

        await page.DisconnectLastFmCommand.ExecuteAsync(null);
        Assert.Equal("Not connected", page.LastFmStatus);
        Assert.Null(new LastFmSessionVault(_dir).Load());
    }

    [Fact]
    public async Task WhenTheBrowserWontOpen_TheAddressIsShown()
    {
        using var scrobbler = Scrobbler();
        var page = Page(scrobbler, openUrl: _ => Task.FromResult(false));

        await page.ConnectLastFmCommand.ExecuteAsync(null);

        Assert.Contains("Couldn't open the browser", page.LastFmNotice);
        Assert.Equal("https://www.last.fm/api/auth/?api_key=key&token=tok", page.LastFmAuthorizeUrl);
    }

    [Fact]
    public async Task TheBrowserNotice_SurvivesARefreshFromTheScrobbler()
    {
        // The scrobbler's Changed event posts a refresh, which can run after the
        // notice is set; it used to blank the notice then.
        using var scrobbler = Scrobbler();
        var page = Page(scrobbler, openUrl: _ => Task.FromResult(false));
        await page.ConnectLastFmCommand.ExecuteAsync(null);

        page.RefreshLastFm();

        Assert.Contains("Couldn't open the browser", page.LastFmNotice);
    }

    [Fact]
    public async Task TheBrowserNotice_GoesOnceTheWaitEnds()
    {
        using var scrobbler = Scrobbler();
        var page = Page(scrobbler, openUrl: _ => Task.FromResult(false));
        await page.ConnectLastFmCommand.ExecuteAsync(null);

        await page.CancelLastFmCommand.ExecuteAsync(null);

        Assert.Equal("", page.LastFmNotice);
    }

    [Fact]
    public void ScrobblingToggle_ReachesTheScrobblerAndSaves()
    {
        using var scrobbler = Scrobbler();
        var app = new AppSettings();
        var saves = 0;
        var page = Page(scrobbler, app, () => saves++);

        page.LastFmScrobbling = false;

        Assert.False(app.LastFmScrobbling);
        Assert.False(scrobbler.ScrobblingEnabled);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void Vault_IsVersionedJson_InAnOwnerOnlyFile()
    {
        var vault = new LastFmSessionVault(_dir);
        vault.Save(new LastFmSession("DemoListener", "SK"));

        var path = Path.Combine(_dir, "lastfm-session.bin");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal("""{"version":1,"username":"DemoListener","key":"SK"}""", File.ReadAllText(path));
        }
        Assert.Equal(new LastFmSession("DemoListener", "SK"), vault.Load());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"version":2,"username":"a","key":"b"}""")]
    [InlineData("""{"version":1,"username":"","key":"b"}""")]
    public void Vault_UnreadableOrNewer_IsNoSession(string contents)
    {
        if (OperatingSystem.IsWindows())
            return; // DPAPI-wrapped there; plaintext fixtures don't apply
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "lastfm-session.bin"), contents);

        Assert.Null(new LastFmSessionVault(_dir).Load());
    }

    [Fact]
    public async Task WhileWaiting_EveryControlHasASpokenName()
    {
        using var scrobbler = Scrobbler();
        await scrobbler.BeginConnectAsync();

        var names = SpokenNames(scrobbler, page =>
            page.LastFmAuthorizeUrl = "https://www.last.fm/api/auth/?api_key=key&token=tok");

        Assert.Contains("Finish connecting", names);
        Assert.Contains("Cancel", names);
        Assert.Contains("https://www.last.fm/api/auth/?api_key=key&token=tok", names);  // read out as text
        Assert.DoesNotContain("Connect", names);   // replaced by Finish while waiting
        Assert.DoesNotContain("Scrobble what I play", names);
    }

    [Fact]
    public async Task WhenConnected_TheToggleAndDisconnectAreNamed()
    {
        new LastFmSessionVault(_dir).Save(new LastFmSession("DemoListener", "SK"));
        using var scrobbler = Scrobbler();
        await scrobbler.WhenIdleAsync();

        var names = SpokenNames(scrobbler);

        Assert.Contains("Disconnect", names);
        Assert.Contains("Scrobble what I play", names);
        Assert.DoesNotContain("Connect", names);
    }

    /// <summary>Last.fm ended the connection in an earlier run, with one play kept.</summary>
    private void SeedEndedByLastFm()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "scrobble-queue.json"), """
            {"version":1,"sessionEnded":true,"scrobbles":[{"artist":"Daft Punk","track":"Instant Crush","timestamp":1700000000}]}
            """);
    }

    [Fact]
    public async Task EndedByLastFm_OffersConnectAndDisconnect_AndShowsWhatIsKept()
    {
        SeedEndedByLastFm();
        using var scrobbler = Scrobbler();
        await scrobbler.WhenIdleAsync();
        var page = Page(scrobbler);

        Assert.Equal("Connection ended by Last.fm", page.LastFmStatus);
        Assert.True(page.LastFmShowConnect);
        Assert.True(page.LastFmShowDisconnect);
        Assert.True(page.LastFmRecording);
        Assert.Contains("still records what you play", page.LastFmNotice);
        Assert.Equal("1 scrobble is waiting to be sent. Disconnecting discards them.", page.LastFmPending);

        await page.DisconnectLastFmCommand.ExecuteAsync(null);

        Assert.Equal("Not connected", page.LastFmStatus);
        Assert.False(page.LastFmShowDisconnect);
        Assert.False(page.LastFmRecording);
        Assert.Equal("", page.LastFmNotice);
        Assert.Equal("", page.LastFmPending);
    }

    [Fact]
    public async Task EndedByLastFm_EveryControlIsNamed()
    {
        SeedEndedByLastFm();
        using var scrobbler = Scrobbler();
        await scrobbler.WhenIdleAsync();

        var names = SpokenNames(scrobbler);

        Assert.Contains("Connect", names);
        Assert.Contains("Disconnect", names);
        Assert.Contains("Scrobble what I play", names);
    }

    /// <summary>What a screen reader announces for the page's visible inputs;
    /// fails on any it would announce without a name.</summary>
    private static string[] SpokenNames(LastFmScrobbler scrobbler, Action<SettingsPageViewModel>? arrange = null)
    {
        string[] names = [];
        Headless.Run(() =>
        {
            AccessibleNames.Install();
            ShellResources.Ensure();
            var page = Page(scrobbler);
            arrange?.Invoke(page);
            var view = new SettingsPage { DataContext = page };
            var window = new Window { Width = 900, Height = 1400, Content = view };
            window.Show();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            static string? Spoken(Control c) =>
                Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(c).GetName();

            var controls = view.GetVisualDescendants()
                .Where(v => v is Button or ToggleSwitch or SelectableTextBlock)
                .Cast<Control>()
                .Where(c => c.IsEffectivelyVisible && c.TemplatedParent is null)
                .ToArray();
            Assert.Empty(controls.Where(c => string.IsNullOrWhiteSpace(Spoken(c)))
                .Select(c => $"{c.GetType().Name} {c.Name}"));
            names = controls.Select(c => Spoken(c)!).ToArray();
            window.Close();
        });
        return names;
    }

    [Fact]
    public void WithoutKeys_ConnectIsDisabled_AndNoToggleSuggestsScrobbling() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var view = new SettingsPage { DataContext = Page(null) };
        var window = new Window { Width = 900, Height = 1400, Content = view };
        window.Show();
        window.UpdateLayout();

        var connect = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Connect");
        var toggle = view.GetVisualDescendants().OfType<ToggleSwitch>()
            .Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Scrobble what I play");
        Assert.True(connect.IsVisible);
        Assert.False(connect.IsEffectivelyEnabled);
        Assert.False(toggle.IsEffectivelyVisible);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),
            b => b.Content as string == "Disconnect" && b.IsEffectivelyVisible);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == SettingsPageViewModel.LastFmUnavailableText);
        window.Close();
    });
}
