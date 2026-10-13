using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Rambutan.Core.Remote;

namespace Rambutan.LocalApi.Tests;

internal sealed class FakeRemoteLibrary : IRemoteLibrary
{
    public readonly List<RemoteQueueCommand> Commands = [];
    public readonly List<(string VideoId, bool Next)> Added = [];
    public readonly List<string> Queries = [];
    public RemoteQueue Queue { get; set; } = new(0, -1, 0, []);
    public Func<string, CancellationToken, Task<RemoteSearchOutcome>> Search { get; set; } =
        (_, _) => Task.FromResult(new RemoteSearchOutcome([]));

    public RemoteQueue GetQueue() => Queue;

    public void Execute(RemoteQueueCommand command)
    {
        lock (Commands) Commands.Add(command);
    }

    public Task<RemoteSearchOutcome> SearchAsync(string query, CancellationToken ct)
    {
        lock (Queries) Queries.Add(query);
        return Search(query, ct);
    }

    public bool Add(string videoId, bool next)
    {
        if (videoId == "unknown")
            return false;
        lock (Added) Added.Add((videoId, next));
        return true;
    }

    public readonly List<string> Played = [];
    public readonly List<string> MoreAsked = [];

    public Task<RemoteSearchOutcome?> MoreAsync(string more, CancellationToken ct)
    {
        lock (MoreAsked) MoreAsked.Add(more);
        return Task.FromResult(more == "known"
            ? new RemoteSearchOutcome([new RemoteSearchResult("p2", "Page two", "Artist", null, null)])
            : null);
    }

    public bool Play(string videoId)
    {
        if (videoId == "unknown")
            return false;
        lock (Played) Played.Add(videoId);
        return true;
    }

    public bool HasCover(string videoId) => videoId == "queued";
}

/// <summary>The queue and search routes the phone remote uses.</summary>
public sealed class LibraryEndpointsTests : IAsyncLifetime
{
    private const string Token = "test-token-0123456789-abcdefghijklmnopqrstu";
    private readonly FakeRemoteLibrary _library = new();
    private LocalApiHost _host = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await StartAsync(_library);
        _http = Client(_host);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _host.DisposeAsync();
    }

    private static async Task<LocalApiHost> StartAsync(IRemoteLibrary? library)
    {
        var started = await LocalApiHost.StartAsync(new LocalApiOptions
        {
            Address = IPAddress.Loopback,
            Port = 0,
            Player = new FakeRemotePlayer(),
            Library = library,
            Token = () => Token,
            Artwork = (id, size, _) => Task.FromResult<RemoteArtwork?>(
                new RemoteArtwork(Encoding.UTF8.GetBytes(size == ArtworkSize.Row ? id + " (row)" : id), "image/png")),
        });
        return started.Host ?? throw new InvalidOperationException(started.Detail);
    }

    private static HttpClient Client(LocalApiHost host)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return http;
    }

    private Task<HttpResponseMessage> PostAsync(string path, string json) =>
        _http.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    [Theory]
    [InlineData("GET", "/api/v1/queue")]
    [InlineData("GET", "/api/v1/search?q=x")]
    [InlineData("POST", "/api/v1/queue/jump")]
    [InlineData("POST", "/api/v1/queue/remove")]
    [InlineData("POST", "/api/v1/queue/move")]
    [InlineData("POST", "/api/v1/queue/add")]
    public async Task TheRoutes_RefuseAClientWithoutTheToken(string method, string path)
    {
        using var anonymous = new HttpClient { BaseAddress = _http.BaseAddress };
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = new StringContent("""{"index":0,"from":0,"to":1,"videoId":"a"}""", Encoding.UTF8, "application/json");

        var response = await anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_library.Commands);
        Assert.Empty(_library.Added);
        Assert.Empty(_library.Queries);
    }

    [Fact]
    public async Task WithoutALibrary_TheRoutesDoNotExist()
    {
        await using var host = await StartAsync(null);
        using var http = Client(host);

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v1/queue")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v1/search?q=x")).StatusCode);
    }

    [Fact]
    public async Task Queue_IsTheLibrarysWindow()
    {
        _library.Queue = new RemoteQueue(7, 1, 40, [
            new RemoteQueueItem(0, "a", "A", "Artist", 1000, false),
            new RemoteQueueItem(1, "b", "B", "Artist", null, true),
        ]);

        var response = await _http.GetAsync("/api/v1/queue");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(7, json.GetProperty("version").GetInt64());
        Assert.Equal(1, json.GetProperty("currentIndex").GetInt32());
        Assert.Equal(40, json.GetProperty("total").GetInt32());
        var second = json.GetProperty("items")[1];
        Assert.Equal("b", second.GetProperty("videoId").GetString());
        Assert.Equal(1, second.GetProperty("index").GetInt32());
        Assert.True(second.GetProperty("autoplay").GetBoolean());
    }

    [Fact]
    public async Task QueueCommands_ReachTheLibrary()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync("/api/v1/queue/jump", """{"index":2,"videoId":"c"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync("/api/v1/queue/remove", """{"index":1,"videoId":"b"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync("/api/v1/queue/move", """{"from":3,"videoId":"d","to":1}""")).StatusCode);

        Assert.Equal<RemoteQueueCommand>(
            [
                new RemoteQueueCommand.Jump(2, "c"),
                new RemoteQueueCommand.Remove(1, "b"),
                new RemoteQueueCommand.Move(3, "d", 1),
            ],
            _library.Commands);
    }

    [Theory]
    [InlineData("/api/v1/queue/jump", "")]
    [InlineData("/api/v1/queue/jump", "not json")]
    [InlineData("/api/v1/queue/jump", """{"index":2}""")]
    [InlineData("/api/v1/queue/remove", """{"index":"x","videoId":"a"}""")]
    [InlineData("/api/v1/queue/move", """{"from":1,"to":2,"videoId":" "}""")]
    [InlineData("/api/v1/queue/add", """{"next":true}""")]
    [InlineData("/api/v1/queue/add", """{"videoId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    public async Task BadBodies_AreRefused(string path, string body)
    {
        var response = await PostAsync(path, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_library.Commands);
        Assert.Empty(_library.Added);
    }

    [Fact]
    public async Task Add_QueuesAFoundSong()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync("/api/v1/queue/add", """{"videoId":"s1","next":true}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync("/api/v1/queue/add", """{"videoId":"s2"}""")).StatusCode);

        Assert.Equal([("s1", true), ("s2", false)], _library.Added);
    }

    [Fact]
    public async Task Add_ASongNoSearchFound_Is404()
    {
        var response = await PostAsync("/api/v1/queue/add", """{"videoId":"unknown"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Search_ReturnsTheResults()
    {
        _library.Search = (q, _) => Task.FromResult(new RemoteSearchOutcome(
            [new RemoteSearchResult("s1", q, "Artist", null, 2000)]));

        var response = await _http.GetAsync("/api/v1/search?q=%20daft%20punk%20");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["daft punk"], _library.Queries); // trimmed
        var first = json.GetProperty("results")[0];
        Assert.Equal("s1", first.GetProperty("videoId").GetString());
        Assert.Equal("daft punk", first.GetProperty("title").GetString());
        Assert.Equal(2000, first.GetProperty("durationMs").GetInt64());
    }

    [Theory]
    [InlineData("/api/v1/search")]
    [InlineData("/api/v1/search?q=")]
    [InlineData("/api/v1/search?q=%20%20")]
    public async Task Search_ABlankQuery_IsRefused(string path)
    {
        var response = await _http.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_library.Queries);
    }

    [Fact]
    public async Task Search_AnOverlongQuery_IsRefused()
    {
        var response = await _http.GetAsync("/api/v1/search?q=" + new string('a', LibraryEndpoints.MaxQueryLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_library.Queries);
    }

    [Theory]
    [InlineData(RemoteSearchFailure.Offline, "offline")]
    [InlineData(RemoteSearchFailure.Failed, "failed")]
    public async Task Search_AFailure_SaysWhichWithoutDetail(RemoteSearchFailure failure, string expected)
    {
        _library.Search = (_, _) => Task.FromResult(new RemoteSearchOutcome([], failure));

        var response = await _http.GetAsync("/api/v1/search?q=x");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal($$"""{"error":"{{expected}}"}""", body);
    }

    [Fact]
    public async Task Search_OneAtATimeReachesTheCatalog()
    {
        var release = new TaskCompletionSource();
        var running = 0;
        var most = 0;
        _library.Search = async (_, _) =>
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            await release.Task;
            Interlocked.Decrement(ref running);
            return new RemoteSearchOutcome([]);
        };

        var first = _http.GetAsync("/api/v1/search?q=a");
        var second = _http.GetAsync("/api/v1/search?q=b");
        await WaitUntilAsync(() => _library.Queries.Count >= 1);
        await Task.Delay(100);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, most);
        Assert.Equal(2, _library.Queries.Count);
    }

    [Fact]
    public async Task Play_StartsASongASearchFound()
    {
        var played = await PostAsync("/api/v1/queue/play", """{"videoId":"s1"}""");
        var unknown = await PostAsync("/api/v1/queue/play", """{"videoId":"unknown"}""");
        var blank = await PostAsync("/api/v1/queue/play", """{"videoId":""}""");

        Assert.Equal(HttpStatusCode.Accepted, played.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(["s1"], _library.Played);
    }

    [Fact]
    public async Task Search_SaysHowToAskForMore()
    {
        _library.Search = (_, _) => Task.FromResult(new RemoteSearchOutcome([], More: "known"));

        var json = JsonDocument.Parse(await _http.GetStringAsync("/api/v1/search?q=x")).RootElement;

        Assert.Equal("known", json.GetProperty("more").GetString());
    }

    [Fact]
    public async Task More_ReturnsTheNextPage_Or404WhenTheKeyIsGone()
    {
        var next = await _http.GetAsync("/api/v1/search?more=known");
        var gone = await _http.GetAsync("/api/v1/search?more=forgotten");
        var json = JsonDocument.Parse(await next.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("p2", json.GetProperty("results")[0].GetProperty("videoId").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("more").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(["known", "forgotten"], _library.MoreAsked);
        Assert.Empty(_library.Queries);
    }

    [Fact]
    public async Task Artwork_ServesTracksTheLibraryKnows_AndNoOthers()
    {
        var queued = await _http.GetAsync("/api/v1/artwork?v=queued");
        var other = await _http.GetAsync("/api/v1/artwork?v=elsewhere");

        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        Assert.Equal("queued", await queued.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task Artwork_ForARow_AsksForASmallCover()
    {
        var row = await _http.GetStringAsync("/api/v1/artwork?v=queued&size=row");
        var other = await _http.GetStringAsync("/api/v1/artwork?v=queued&size=huge");

        Assert.Equal("queued (row)", row);
        Assert.Equal("queued", other);
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
