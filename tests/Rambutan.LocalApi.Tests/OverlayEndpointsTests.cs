using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Rambutan.Core.Remote;
using Rambutan.LocalApi.Overlay;

namespace Rambutan.LocalApi.Tests;

/// <summary>The OBS overlay's host: loopback, its own read-only token,
/// the style and the state on one stream, the raw bands on another.</summary>
public sealed class OverlayEndpointsTests : IAsyncLifetime
{
    private const string OverlayToken = "overlay-token-0123456789-abcdefghijklmnopq";
    private const string RemoteToken = "remote-token-0123456789-abcdefghijklmnopqr";

    private readonly FakeRemotePlayer _player = new();
    private readonly OverlayConfigSource _config = new(OverlayConfig.Default);
    private readonly float[] _bands = new float[OverlayOptions.BandCount];
    private long _frame = -1;
    private LocalApiHost _host = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await StartAsync(serveRemote: false);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.Port}") };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _host.DisposeAsync();
    }

    private async Task<LocalApiHost> StartAsync(bool serveRemote)
    {
        var started = await LocalApiHost.StartAsync(new LocalApiOptions
        {
            Address = IPAddress.Loopback,
            Port = 0,
            Player = _player,
            Token = () => serveRemote ? RemoteToken : null,
            ServeRemote = serveRemote,
            Artwork = (id, _, _) => Task.FromResult<RemoteArtwork?>(
                id == "art" ? new RemoteArtwork([4, 5, 6], "image/png") : null),
            Overlay = new OverlayOptions
            {
                Token = () => OverlayToken,
                Config = _config,
                Bands = destination =>
                {
                    lock (_bands)
                    {
                        _bands.CopyTo(destination);
                        return _frame;
                    }
                },
            },
        });
        return started.Host ?? throw new InvalidOperationException(started.Detail);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token = OverlayToken)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method == HttpMethod.Post)
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return request;
    }

    [Fact]
    public async Task ThePage_IsServedWithoutAToken()
    {
        var response = await _http.GetAsync("/overlay/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("<div id=\"app\">", await response.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task TheAppsFont_IsServedWithThePage()
    {
        // The stylesheet names the hashed font files the build copied.
        var page = await _http.GetStringAsync("/overlay/");
        var css = System.Text.RegularExpressions.Regex.Match(page, "/overlay/assets/[^\"]+\\.css").Value;
        var font = System.Text.RegularExpressions.Regex.Match(
            await _http.GetStringAsync(css), "/overlay/assets/Figtree-Regular-[^)\"']+\\.ttf").Value;

        var response = await _http.GetAsync(font);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("font/ttf", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task TheRoot_SendsYouToTheOverlay()
    {
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = _http.BaseAddress,
        };

        var response = await noRedirect.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/overlay/", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("/api/v1/overlay/config")]
    [InlineData("/api/v1/overlay/events")]
    [InlineData("/api/v1/overlay/bands")]
    [InlineData("/api/v1/overlay/artwork")]
    public async Task TheOverlayApi_RefusesAClientWithoutItsToken(string path)
    {
        foreach (var token in new string?[] { null, "", "wrong", OverlayToken + "x", RemoteToken })
        {
            var response = await _http.SendAsync(Request(HttpMethod.Get, path, token));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("GET", "/remote/")]
    [InlineData("GET", "/api/v1/state")]
    [InlineData("GET", "/api/v1/events")]
    [InlineData("POST", "/api/v1/player/play")]
    [InlineData("POST", "/api/v1/player/next")]
    public async Task TheOverlaysHost_ServesNothingOfTheRemote(string method, string path)
    {
        foreach (var token in new string?[] { null, OverlayToken, RemoteToken })
        {
            var response = await _http.SendAsync(Request(new HttpMethod(method), path, token));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        Assert.Empty(_player.Commands);
    }

    [Fact]
    public async Task TheOverlayToken_ControlsNothing_EvenOnAHostThatServesTheRemote()
    {
        await using var shared = await StartAsync(serveRemote: true);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{shared.Port}") };

        foreach (var action in new[] { "play", "pause", "next", "previous", "seek", "volume" })
        {
            var response = await http.SendAsync(Request(HttpMethod.Post, $"/api/v1/player/{action}", OverlayToken));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Empty(_player.Commands);

        // And the remote's token doesn't read the overlay's API: each has its own.
        var config = await http.SendAsync(Request(HttpMethod.Get, "/api/v1/overlay/config", RemoteToken));
        Assert.Equal(HttpStatusCode.Unauthorized, config.StatusCode);
        var ok = await http.SendAsync(Request(HttpMethod.Get, "/api/v1/overlay/config"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Config_IsTheCurrentStyle()
    {
        _config.Current = OverlayConfig.Default with { Align = OverlayAlign.Right };

        var response = await _http.SendAsync(Request(HttpMethod.Get, "/api/v1/overlay/config"));
        var parsed = OverlayConfigFormat.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OverlayAlign.Right, parsed.Config!.Align);
    }

    [Fact]
    public async Task Events_SendTheStyleAndState_ThenEachChange()
    {
        using var response = await _http.SendAsync(
            Request(HttpMethod.Get, "/api/v1/overlay/events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        var (name, data) = await NextEventAsync(reader);
        Assert.Equal("config", name);
        Assert.Equal("rambutan-overlay", data.GetProperty("format").GetString());
        (name, data) = await NextEventAsync(reader);
        Assert.Equal("state", name);
        Assert.Equal("stopped", data.GetProperty("playback").GetString());
        await WaitUntilAsync(() => _host.OverlayClients == 1);
        Assert.Equal(0, _host.ConnectedClients);

        _player.Current = RemoteState.Empty with
        {
            Playback = "playing",
            Track = new RemoteTrack("v1", "Song", "Artist", null, 1000, false),
        };
        (name, data) = await NextEventAsync(reader);
        Assert.Equal("state", name);
        Assert.Equal("Song", data.GetProperty("track").GetProperty("title").GetString());

        _config.Current = OverlayConfig.Default with { Scale = 2 };
        (name, data) = await NextEventAsync(reader);
        Assert.Equal("config", name);
        Assert.Equal(2, data.GetProperty("scale").GetDouble());
    }

    [Fact]
    public async Task Bands_AreSentAsTheyAre_OncePerNewFrame()
    {
        OverlayEndpoints.BandInterval = TimeSpan.FromMilliseconds(10);
        lock (_bands)
        {
            _bands[0] = 0.5f;
            _bands[31] = 1f;
            _frame = 7;
        }

        using var response = await _http.SendAsync(
            Request(HttpMethod.Get, "/api/v1/overlay/bands"), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        var (name, data) = await NextEventAsync(reader);
        Assert.Equal("bands", name);
        Assert.Equal(7, data.GetProperty("frame").GetInt64());
        var bands = data.GetProperty("bands").EnumerateArray().Select(b => b.GetDouble()).ToArray();
        Assert.Equal(OverlayOptions.BandCount, bands.Length);
        Assert.Equal(0.5, bands[0]);
        Assert.Equal(1, bands[31]);

        lock (_bands)
        {
            _bands[0] = 0.25f;
            _frame = 8;
        }
        (_, data) = await NextEventAsync(reader);
        // Frame 7 isn't sent twice: the next event is the next frame.
        Assert.Equal(8, data.GetProperty("frame").GetInt64());
        Assert.Equal(0.25, data.GetProperty("bands")[0].GetDouble());
    }

    [Fact]
    public void ABandsEvent_IsCompactAndInRange()
    {
        var text = OverlayEndpoints.BandsEvent(3, [0.12345f, -1f, 2f, float.NaN]);

        Assert.Equal("event: bands\ndata: {\"frame\":3,\"bands\":[0.123,0,1,0]}\n\n", text);
    }

    [Fact]
    public async Task Artwork_IsOnlyThePlayingTracksCover()
    {
        var none = await _http.SendAsync(Request(HttpMethod.Get, "/api/v1/overlay/artwork"));
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        _player.Current = RemoteState.Empty with { Track = new RemoteTrack("art", "T", "A", null, null, true) };
        var cover = await _http.SendAsync(Request(HttpMethod.Get, "/api/v1/overlay/artwork?v=art"));

        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
        Assert.Equal("image/png", cover.Content.Headers.ContentType!.MediaType);
        Assert.Equal([4, 5, 6], await cover.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task DisconnectClients_EndsTheOverlaysStreams()
    {
        using var response = await _http.SendAsync(
            Request(HttpMethod.Get, "/api/v1/overlay/events"), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        await NextEventAsync(reader);
        await WaitUntilAsync(() => _host.OverlayClients == 1);

        _host.DisconnectClients();

        await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => _host.OverlayClients == 0);
    }

    private static async Task<(string Name, JsonElement Data)> NextEventAsync(StreamReader reader)
    {
        var timeout = TimeSpan.FromSeconds(5);
        string? name = null;
        while (true)
        {
            var line = await reader.ReadLineAsync().WaitAsync(timeout) ?? throw new EndOfStreamException();
            if (line.StartsWith("event: ", StringComparison.Ordinal))
                name = line["event: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                return (name ?? "message", JsonDocument.Parse(line["data: ".Length..]).RootElement);
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
