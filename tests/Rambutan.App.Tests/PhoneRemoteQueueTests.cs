using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Rambutan.App.Services;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;
using Rambutan.Core.Remote;

namespace Rambutan.App.Tests;

/// <summary>The phone's queue and search, end to end through the service the app
/// composes: a search on the phone reaches the catalog, and what it found reaches
/// the queue. Loopback only.</summary>
public sealed class PhoneRemoteQueueTests : IAsyncDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rambutan-remote-");
    private readonly QueueModel _queue = new();
    private readonly PhoneRemoteService _remote;
    private readonly List<string> _queries = [];

    public PhoneRemoteQueueTests()
    {
        _remote = new PhoneRemoteService(
            FakePlayback.Coordinator(_queue), action => action(), new RemoteTokenVault(_dir.FullName),
            new CoverSource((url, _) => Task.FromResult(Encoding.UTF8.GetBytes(url)), _ => null), IPAddress.Loopback,
            searchSongs: (query, _, _) =>
            {
                _queries.Add(query);
                return Task.FromResult(new RemoteSongPage(
                [
                    new TrackInfo
                    {
                        VideoId = "found1", Title = "Found one", Artists = [new("Someone")],
                        ThumbnailUrl = "https://example.test/found1.jpg",
                    },
                ]));
            });
    }

    public async ValueTask DisposeAsync()
    {
        await _remote.StopAsync();
        _dir.Delete(recursive: true);
    }

    [Fact]
    public async Task ASongFoundOnThePhone_CanBeQueued()
    {
        _queue.SetQueue([new TrackInfo { VideoId = "playing", Title = "Playing", Artists = [] }]);
        await _remote.SetEnabledAsync(true);
        using var http = Client();

        var search = await http.GetAsync("/api/v1/search?q=someone");
        var results = JsonDocument.Parse(await search.Content.ReadAsStringAsync()).RootElement.GetProperty("results");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        Assert.Equal(["someone"], _queries);
        Assert.Equal("found1", results[0].GetProperty("videoId").GetString());

        var add = await http.PostAsync("/api/v1/queue/add",
            new StringContent("""{"videoId":"found1","next":true}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, add.StatusCode);
        Assert.Equal(["playing", "found1"], _queue.Items.Select(t => t.VideoId));

        var queue = JsonDocument.Parse(await http.GetStringAsync("/api/v1/queue")).RootElement;
        Assert.Equal(2, queue.GetProperty("total").GetInt32());
        Assert.Equal("Found one", queue.GetProperty("items")[1].GetProperty("title").GetString());
    }

    [Fact]
    public async Task TheCoverOfAFoundSong_IsServed_ButNotAnyOther()
    {
        await _remote.SetEnabledAsync(true);
        using var http = Client();

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v1/artwork?v=found1")).StatusCode);

        var search = JsonDocument.Parse(await http.GetStringAsync("/api/v1/search?q=someone")).RootElement;
        Assert.True(search.GetProperty("results")[0].GetProperty("hasArtwork").GetBoolean());
        var cover = await http.GetAsync("/api/v1/artwork?v=found1");

        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
        Assert.Equal("https://example.test/found1.jpg", await cover.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v1/artwork?v=elsewhere")).StatusCode);
    }

    private HttpClient Client()
    {
        var link = _remote.LinkFor(IPAddress.Loopback)!;
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_remote.Port}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", link[(link.IndexOf("#t=", StringComparison.Ordinal) + 3)..]);
        return http;
    }
}
