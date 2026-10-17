using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Rambutan.Core.Remote;

namespace Rambutan.LocalApi.Tests;

internal sealed class FakeRemotePlayer : IRemotePlayer
{
    private RemoteState _current = RemoteState.Empty;
    public readonly List<RemoteCommand> Commands = [];

    public RemoteState Current
    {
        get => _current;
        set
        {
            _current = value;
            Changed?.Invoke();
        }
    }

    public event Action? Changed;

    public void Execute(RemoteCommand command)
    {
        lock (Commands) Commands.Add(command);
    }
}

public sealed class LocalApiHostTests : IAsyncLifetime
{
    private readonly FakeRemotePlayer _player = new();
    private string? _token = "test-token-0123456789-abcdefghijklmnopqrstu";
    private LocalApiHost _host = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        var started = await LocalApiHost.StartAsync(Options(port: 0));
        _host = started.Host ?? throw new InvalidOperationException(started.Detail);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.Port}") };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _host.DisposeAsync();
    }

    private LocalApiOptions Options(int port, bool fallBack = false) => new()
    {
        Address = IPAddress.Loopback,
        Port = port,
        FallBackToAnyPort = fallBack,
        Player = _player,
        Token = () => _token,
        Artwork = (id, _, _) => Task.FromResult<RemoteArtwork?>(
            id == "art" ? new RemoteArtwork([1, 2, 3], "image/jpeg") : null),
    };

    private HttpRequestMessage Request(HttpMethod method, string path, string? token = "use-test-token", string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token == "use-test-token" ? _token : token);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    [Fact]
    public async Task ThePage_IsServedWithoutAToken()
    {
        var response = await _http.GetAsync("/remote/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("<div id=\"app\">", await response.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task TheRoot_SendsYouToThePage()
    {
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = _http.BaseAddress,
        };

        var response = await noRedirect.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/remote/", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task AMissingAsset_Is404()
    {
        var response = await _http.GetAsync("/remote/assets/nope.js");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/v1/state")]
    [InlineData("GET", "/api/v1/events")]
    [InlineData("GET", "/api/v1/artwork")]
    [InlineData("POST", "/api/v1/player/play")]
    [InlineData("POST", "/api/v1/player/pause")]
    [InlineData("POST", "/api/v1/player/next")]
    [InlineData("POST", "/api/v1/player/previous")]
    [InlineData("POST", "/api/v1/player/seek")]
    [InlineData("POST", "/api/v1/player/volume")]
    [InlineData("GET", "/api/v1/anything-else")]
    public async Task TheApi_RefusesAClientWithoutTheToken(string method, string path)
    {
        foreach (var token in new string?[] { null, "wrong", _token + "x", "" })
        {
            var response = await _http.SendAsync(Request(new HttpMethod(method), path, token, json: "{}"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("", await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(_player.Commands);
    }

    [Fact]
    public async Task NoTokenIssued_RefusesEveryone()
    {
        _token = null;

        var response = await _http.SendAsync(Request(HttpMethod.Get, "/api/v1/state", token: ""));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ARotatedToken_AppliesAtOnce()
    {
        var old = _token;
        _token = "rotated-token-0123456789-abcdefghijklmnopq";

        var response = await _http.SendAsync(Request(HttpMethod.Get, "/api/v1/state", token: old));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task State_IsTheTrackAndPlayback()
    {
        _player.Current = new RemoteState(
            "playing", new RemoteTrack("v1", "Song", "Artist", "Album", 200_000, true), 1500, 200_000, 0.5, false,
            Accent: "#5B8DEF");

        var response = await _http.SendAsync(Request(HttpMethod.Get, "/api/v1/state"));
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("playing", json.GetProperty("playback").GetString());
        Assert.Equal("Song", json.GetProperty("track").GetProperty("title").GetString());
        Assert.Equal(1500, json.GetProperty("positionMs").GetInt64());
        Assert.Equal(0.5, json.GetProperty("volume").GetDouble());
        // The remote page reads state.accent by this name.
        Assert.Equal("#5B8DEF", json.GetProperty("accent").GetString());
    }

    [Fact]
    public async Task Commands_ReachThePlayer()
    {
        foreach (var action in new[] { "play", "pause", "next", "previous" })
        {
            var response = await _http.SendAsync(Request(HttpMethod.Post, $"/api/v1/player/{action}"));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        await _http.SendAsync(Request(HttpMethod.Post, "/api/v1/player/seek", json: """{"positionMs":61000}"""));
        await _http.SendAsync(Request(HttpMethod.Post, "/api/v1/player/volume", json: """{"volume":0.3}"""));

        Assert.Equal(
            [
                new RemoteCommand.Play(), new RemoteCommand.Pause(), new RemoteCommand.Next(), new RemoteCommand.Previous(),
                new RemoteCommand.Seek(TimeSpan.FromSeconds(61)), new RemoteCommand.SetVolume(0.3),
            ],
            _player.Commands);
    }

    [Theory]
    [InlineData("seek", "not json")]
    [InlineData("seek", """{"positionMs":-5}""")]
    [InlineData("volume", """{"volume":"loud"}""")]
    [InlineData("volume", "")]
    public async Task BadCommandBodies_AreRefused(string action, string body)
    {
        var response = await _http.SendAsync(Request(HttpMethod.Post, $"/api/v1/player/{action}", json: body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_player.Commands);
    }

    [Fact]
    public async Task Volume_IsClampedToTheRange()
    {
        await _http.SendAsync(Request(HttpMethod.Post, "/api/v1/player/volume", json: """{"volume":7}"""));

        Assert.Equal([new RemoteCommand.SetVolume(1)], _player.Commands);
    }

    [Fact]
    public async Task Artwork_IsOnlyThePlayingTracksCover()
    {
        var none = await _http.SendAsync(Request(HttpMethod.Get, "/api/v1/artwork"));
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        _player.Current = RemoteState.Empty with { Track = new RemoteTrack("art", "T", "A", null, null, true) };
        var cover = await _http.SendAsync(Request(HttpMethod.Get, "/api/v1/artwork?v=art"));

        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
        Assert.Equal("image/jpeg", cover.Content.Headers.ContentType!.MediaType);
        Assert.Equal([1, 2, 3], await cover.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Events_SendTheStateNowAndOnEachChange()
    {
        using var response = await _http.SendAsync(
            Request(HttpMethod.Get, "/api/v1/events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        var first = await NextStateAsync(reader);
        Assert.Equal("stopped", first.GetProperty("playback").GetString());
        await WaitUntilAsync(() => _host.ConnectedClients == 1);

        _player.Current = RemoteState.Empty with { Playback = "paused", Volume = 0.2 };
        var second = await NextStateAsync(reader);

        Assert.Equal("paused", second.GetProperty("playback").GetString());
        Assert.Equal(0.2, second.GetProperty("volume").GetDouble());
    }

    [Fact]
    public async Task DisconnectClients_EndsTheStreams()
    {
        using var response = await _http.SendAsync(
            Request(HttpMethod.Get, "/api/v1/events"), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        await NextStateAsync(reader);
        await WaitUntilAsync(() => _host.ConnectedClients == 1);

        _host.DisconnectClients();

        var rest = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("", rest.Trim());
        await WaitUntilAsync(() => _host.ConnectedClients == 0);
    }

    [Fact]
    public async Task Stopping_DoesNotWaitForAnOpenStream()
    {
        // A phone holding the event stream must not hold up the app's exit.
        var host = (await LocalApiHost.StartAsync(Options(0))).Host!;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        using var response = await http.SendAsync(
            Request(HttpMethod.Get, "/api/v1/events"), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        await NextStateAsync(reader);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await host.DisposeAsync();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"stopping took {watch.Elapsed}");
    }

    [Fact]
    public async Task ABusyPort_IsReported()
    {
        var result = await LocalApiHost.StartAsync(Options(_host.Port));

        Assert.Null(result.Host);
        Assert.Equal(LocalApiFailure.PortInUse, result.Failure);
    }

    [Fact]
    public async Task ABusyPort_CanFallBackToAnother()
    {
        var result = await LocalApiHost.StartAsync(Options(_host.Port, fallBack: true));
        await using var host = result.Host;

        Assert.NotNull(host);
        Assert.True(result.PreferredPortBusy);
        Assert.NotEqual(_host.Port, host.Port);
    }

    [Fact]
    public async Task Stopping_ReleasesThePort()
    {
        var first = (await LocalApiHost.StartAsync(Options(0))).Host!;
        var port = first.Port;
        await first.DisposeAsync();

        var again = await LocalApiHost.StartAsync(Options(port));
        await using var host = again.Host;

        Assert.NotNull(host);
        Assert.Equal(port, host.Port);
    }

    [Fact]
    public async Task TheHost_ListensOnlyWhereItIsTold()
    {
        // Bound to loopback, so this machine's other addresses refuse the connection.
        var lan = LanAddresses.Discover().FirstOrDefault();
        if (lan is null)
            return; // no network here: nothing to show

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var refused = await Assert.ThrowsAnyAsync<SocketException>(async () =>
            await socket.ConnectAsync(new IPEndPoint(lan, _host.Port)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);
    }

    private static async Task<JsonElement> NextStateAsync(StreamReader reader)
    {
        var timeout = TimeSpan.FromSeconds(5);
        while (true)
        {
            var line = await reader.ReadLineAsync().WaitAsync(timeout)
                       ?? throw new EndOfStreamException();
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                return JsonDocument.Parse(line["data: ".Length..]).RootElement;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}

public class LanAddressesTests
{
    private static LanCandidate C(string ip, string name = "Wi-Fi", bool gateway = true) =>
        new(IPAddress.Parse(ip), name, gateway);

    [Fact]
    public void ThePhysicalNetworkWithAGateway_ComesFirst()
    {
        var ranked = LanAddresses.Rank(
        [
            C("172.27.80.1", "vEthernet (WSL (Hyper-V firewall))", gateway: false),
            C("192.168.56.1", "VirtualBox Host-Only Network", gateway: false),
            C("192.168.1.23", "Wi-Fi Intel(R) Wi-Fi 6E"),
        ]);

        Assert.Equal(IPAddress.Parse("192.168.1.23"), ranked[0]);
        Assert.Equal(3, ranked.Count);
    }

    [Fact]
    public void LoopbackLinkLocalAndIpv6_AreLeftOut()
    {
        var ranked = LanAddresses.Rank(
        [
            C("127.0.0.1"),
            C("169.254.10.2"),
            C("fe80::1"),
            C("10.0.0.5"),
        ]);

        Assert.Equal([IPAddress.Parse("10.0.0.5")], ranked);
    }

    [Fact]
    public void AVirtualAdapter_GoesAfterAPhysicalOne()
    {
        var ranked = LanAddresses.Rank(
        [
            C("10.0.75.1", "vEthernet (Default Switch)"),
            C("10.0.0.8", "Ethernet Realtek"),
        ]);

        Assert.Equal(IPAddress.Parse("10.0.0.8"), ranked[0]);
    }

    [Fact]
    public void NoNetwork_IsAnEmptyList() => Assert.Empty(LanAddresses.Rank([]));
}
