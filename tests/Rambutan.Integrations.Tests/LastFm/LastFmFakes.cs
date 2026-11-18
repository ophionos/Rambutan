using System.Net;
using System.Text;
using Rambutan.Integrations.LastFm;
using Rambutan.Integrations.Tests.Scrobbling;

namespace Rambutan.Integrations.Tests.LastFm;

/// <summary>One recorded POST to the Last.fm endpoint, its form decoded.</summary>
internal sealed record LastFmRequest(Uri Uri, IReadOnlyDictionary<string, string> Form)
{
    public string Method => Form["method"];
}

/// <summary>
/// Stands in for ws.audioscrobbler.com. Each API method answers from its own
/// queue of canned responses; an empty queue answers with the method's
/// synthetic fixture (the documented success shape).
/// </summary>
internal sealed class FakeLastFmHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _responses = [];
    private readonly object _sync = new();

    public List<LastFmRequest> Requests { get; } = [];

    public IEnumerable<LastFmRequest> Calls(string method)
    {
        lock (_sync)
            return Requests.Where(r => r.Method == method).ToArray();
    }

    public void Enqueue(string method, HttpStatusCode status, string body)
    {
        lock (_sync)
        {
            if (!_responses.TryGetValue(method, out var queue))
                _responses[method] = queue = new();
            queue.Enqueue(() => Json(status, body));
        }
    }

    public void EnqueueError(string method, int code, HttpStatusCode status = HttpStatusCode.BadRequest) =>
        Enqueue(method, status, $$"""{"error":{{code}},"message":"synthetic error {{code}}"}""");

    public void EnqueueNetworkFailure(string method)
    {
        lock (_sync)
        {
            if (!_responses.TryGetValue(method, out var queue))
                _responses[method] = queue = new();
            queue.Enqueue(() => throw new HttpRequestException("no route to host"));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.Length > 1 ? p[1] : ""));
        var recorded = new LastFmRequest(request.RequestUri!, form);

        Func<HttpResponseMessage>? respond = null;
        lock (_sync)
        {
            Requests.Add(recorded);
            if (_responses.TryGetValue(recorded.Method, out var queue) && queue.Count > 0)
                respond = queue.Dequeue();
        }

        return respond is not null ? respond() : Json(HttpStatusCode.OK, Fixture(DefaultFixture(recorded)));
    }

    private static string DefaultFixture(LastFmRequest request) => request.Method switch
    {
        "track.scrobble" when request.Form.ContainsKey("artist[1]") => "track.scrobble.batch",
        "track.scrobble" => "track.scrobble.single",
        var method => method,
    };

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LastFm", $"{name}.synthetic.json"));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

internal sealed class MemorySessionStore : ILastFmSessionStore
{
    public LastFmSession? Saved { get; set; }
    public int Clears { get; private set; }

    public LastFmSession? Load() => Saved;

    public void Save(LastFmSession session) => Saved = session;

    public void Clear()
    {
        Saved = null;
        Clears++;
    }
}
