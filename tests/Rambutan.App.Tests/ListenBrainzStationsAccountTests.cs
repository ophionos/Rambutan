using System.Net;
using System.Text;
using Rambutan.App.Services;
using Rambutan.Integrations.ListenBrainz;

namespace Rambutan.App.Tests;

/// <summary>The stations' own ListenBrainz token: checked, kept apart, and only for ListenBrainz.</summary>
public sealed class ListenBrainzStationsAccountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-lb-stations-" + Guid.NewGuid().ToString("N"));
    private readonly Stub _server = new();

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

    /// <summary>Answers validate-token: valid only for "good-token"; or fails as told.</summary>
    private sealed class Stub : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public HttpStatusCode? Status { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!);
            if (Status is { } status)
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
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
        () => new ListenBrainzClient(ListenBrainzClient.DefaultServer, "1.0", new HttpClient(_server)));

    [Fact]
    public async Task AValidToken_IsKept_AndSurvivesARestart()
    {
        var account = Account();
        await account.ConnectAsync("  good-token ");

        Assert.True(account.Connected);
        Assert.Equal("DemoListener", account.Username);
        Assert.Equal("good-token", account.Token);
        Assert.Null(account.Notice);
        Assert.All(_server.Requests, u => Assert.Equal("api.listenbrainz.org", u.Host));

        var restarted = Account();
        await restarted.LoadAsync();
        Assert.Equal("good-token", restarted.Token);
    }

    [Fact]
    public async Task AnInvalidToken_IsNotKept_AndSaysSo()
    {
        var account = Account();
        await account.ConnectAsync("bad-token");

        Assert.False(account.Connected);
        Assert.Equal(ListenBrainzStationsNotice.TokenInvalid, account.Notice);
        var restarted = Account();
        await restarted.LoadAsync();
        Assert.False(restarted.Connected);
    }

    [Fact]
    public async Task AnEmptyToken_IsNotSent()
    {
        var account = Account();
        await account.ConnectAsync("   ");

        Assert.Empty(_server.Requests);
        Assert.Equal(ListenBrainzStationsNotice.TokenInvalid, account.Notice);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ListenBrainzStationsNotice.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ListenBrainzStationsNotice.Unreachable)]
    public async Task AFailedCheck_SaysWhy(HttpStatusCode status, ListenBrainzStationsNotice notice)
    {
        _server.Status = status;
        var account = Account();
        await account.ConnectAsync("good-token");

        Assert.False(account.Connected);
        Assert.Equal(notice, account.Notice);
    }

    [Fact]
    public async Task Disconnect_ForgetsTheToken_HereAndInTheVault()
    {
        var account = Account();
        await account.ConnectAsync("good-token");
        await account.DisconnectAsync();

        Assert.False(account.Connected);
        Assert.Null(account.Token);
        var restarted = Account();
        await restarted.LoadAsync();
        Assert.False(restarted.Connected);
    }

    [Fact]
    public async Task ATokenSavedForAnotherServer_IsIgnored()
    {
        ListenBrainzTokenVault.ForStations(_dir).Save(
            new ListenBrainzConnection(new Uri("https://koito.example.org/apis/listenbrainz/"), "relay", "RELAY"));

        var account = Account();
        await account.LoadAsync();

        Assert.False(account.Connected);
        Assert.Null(account.Token);
    }

    [Fact]
    public async Task ARefusedStation_KeepsTheToken_AndAsksForANewOne()
    {
        var account = Account();
        await account.ConnectAsync("good-token");
        var changes = 0;
        account.Changed += () => changes++;

        account.TokenRefused();
        account.TokenRefused();

        Assert.True(account.Connected);
        Assert.Equal(ListenBrainzStationsNotice.TokenRefused, account.Notice);
        Assert.Equal(1, changes);

        await account.ConnectAsync("good-token");
        Assert.Null(account.Notice);
    }
}
