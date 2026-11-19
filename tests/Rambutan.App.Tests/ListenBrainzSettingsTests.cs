using System.Net;
using System.Text;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Integrations.ListenBrainz;
using Rambutan.Integrations.Scrobbling;

namespace Rambutan.App.Tests;

/// <summary>The Settings page's ListenBrainz section and the token vault.</summary>
public sealed class ListenBrainzSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-listenbrainz-app-" + Guid.NewGuid().ToString("N"));
    private readonly StubListenBrainz _server = new();

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

    /// <summary>Answers validate-token in the API's documented shape: valid only for "good-token".</summary>
    private sealed class StubListenBrainz : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!);
            var valid = request.Headers.Authorization?.ToString() == "Token good-token";
            var json = request.RequestUri!.AbsolutePath.EndsWith("/1/validate-token", StringComparison.Ordinal)
                ? valid
                    ? """{"code":200,"message":"Token valid.","valid":true,"user_name":"DemoListener"}"""
                    : """{"code":200,"message":"Token invalid.","valid":false}"""
                : """{"status":"ok"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private ListenBrainzScrobbler Scrobbler()
    {
        var scrobbler = new ListenBrainzScrobbler(
            server => new ListenBrainzClient(server, "1.0", new HttpClient(_server)),
            new ScrobbleQueue(_dir, "listenbrainz-queue.json", "listenbrainz"),
            new ListenBrainzTokenVault(_dir));
        scrobbler.Start();
        return scrobbler;
    }

    [Fact]
    public async Task IsOffByDefault_AndConnectWaitsForAToken()
    {
        using var scrobbler = Scrobbler();
        await scrobbler.WhenIdleAsync();

        var section = new ListenBrainzSettingsViewModel(scrobbler, new AppSettings(), null);

        Assert.Equal("Not connected", section.Status);
        Assert.Equal("https://api.listenbrainz.org/", section.ServerText);
        Assert.True(section.ShowConnect);
        Assert.False(section.CanConnect);
        Assert.False(section.Recording);

        section.Token = "  ";
        Assert.False(section.CanConnect);
        section.Token = "good-token";
        Assert.True(section.CanConnect);
    }

    [Fact]
    public async Task Connect_KeepsTheTokenWithItsServer_AndDisconnectForgetsIt()
    {
        using var scrobbler = Scrobbler();
        var section = new ListenBrainzSettingsViewModel(scrobbler, new AppSettings(), null) { Token = "good-token" };

        await section.ConnectCommand.ExecuteAsync(null);

        Assert.True(section.Connected);
        Assert.Equal("Connected as DemoListener", section.Status);
        Assert.Equal("", section.Token);
        Assert.Equal(
            new ListenBrainzConnection(new Uri("https://api.listenbrainz.org/"), "DemoListener", "good-token"),
            new ListenBrainzTokenVault(_dir).Load());

        await section.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal("Not connected", section.Status);
        Assert.Null(new ListenBrainzTokenVault(_dir).Load());
    }

    [Fact]
    public async Task AServerOfTheUsersOwn_IsAskedAndNamed()
    {
        using var scrobbler = Scrobbler();
        var section = new ListenBrainzSettingsViewModel(scrobbler, new AppSettings(), null)
        {
            Token = "good-token",
            ServerText = "https://music.example.org/apis/listenbrainz",
        };

        await section.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("Connected as DemoListener on music.example.org", section.Status);
        Assert.Contains(new Uri("https://music.example.org/apis/listenbrainz/1/validate-token"), _server.Requests);
        Assert.Equal(new Uri("https://music.example.org/apis/listenbrainz/"), new ListenBrainzTokenVault(_dir).Load()?.Server);
    }

    [Fact]
    public async Task ARefusedToken_SaysSo_AndSavesNothing()
    {
        using var scrobbler = Scrobbler();
        var section = new ListenBrainzSettingsViewModel(scrobbler, new AppSettings(), null) { Token = "bad-token" };

        await section.ConnectCommand.ExecuteAsync(null);

        Assert.False(section.Connected);
        Assert.Equal(NoticeText.For(ListenBrainzNotice.ConnectTokenInvalid), section.Notice);
        Assert.Null(new ListenBrainzTokenVault(_dir).Load());
    }

    [Theory]
    [InlineData("listenbrainz.org", "invalid")]
    [InlineData("ftp://example.org", "invalid")]
    [InlineData("http://192.168.1.5:42010/apis/listenbrainz", "unencrypted")]
    [InlineData("https://api.listenbrainz.org", "")]
    public async Task TheServerField_SaysWhatIsWrongWithIt(string text, string note)
    {
        using var scrobbler = Scrobbler();
        var section = new ListenBrainzSettingsViewModel(scrobbler, new AppSettings(), null)
        {
            Token = "good-token",
            ServerText = text,
        };
        await scrobbler.WhenIdleAsync();

        var expected = note == "" ? "" : Localization.Strings.Get("settings.listenbrainz.server." + note);
        Assert.Equal(expected, section.ServerNote);
        Assert.Equal(note != "invalid", section.CanConnect);
    }

    [Fact]
    public void TheToggle_ReachesTheScrobblerAndSaves()
    {
        using var scrobbler = Scrobbler();
        var app = new AppSettings();
        var saves = 0;
        var section = new ListenBrainzSettingsViewModel(scrobbler, app, () => saves++);

        section.Scrobbling = false;

        Assert.False(app.ListenBrainzScrobbling);
        Assert.False(scrobbler.ScrobblingEnabled);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void Vault_IsVersionedJson_InAnOwnerOnlyFile()
    {
        var vault = new ListenBrainzTokenVault(_dir);
        var connection = new ListenBrainzConnection(new Uri("https://api.listenbrainz.org/"), "DemoListener", "tok");
        vault.Save(connection);

        var path = Path.Combine(_dir, "listenbrainz-token.bin");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(
                """{"version":1,"server":"https://api.listenbrainz.org/","username":"DemoListener","token":"tok"}""",
                File.ReadAllText(path));
        }
        Assert.Equal(connection, vault.Load());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"version":2,"server":"https://api.listenbrainz.org/","username":"a","token":"b"}""")]
    [InlineData("""{"version":1,"server":"https://api.listenbrainz.org/","username":"","token":"b"}""")]
    [InlineData("""{"version":1,"server":"not a url","username":"a","token":"b"}""")]
    [InlineData("""{"version":1,"username":"a","token":"b"}""")]
    public void Vault_UnreadableOrNewer_IsNoConnection(string contents)
    {
        if (OperatingSystem.IsWindows())
            return; // DPAPI-wrapped there; plaintext fixtures don't apply
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "listenbrainz-token.bin"), contents);

        Assert.Null(new ListenBrainzTokenVault(_dir).Load());
    }
}
