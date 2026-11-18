using System.Net;
using System.Text;
using System.Text.Json;
using Rambutan.Integrations.ListenBrainz;

namespace Rambutan.Integrations.Tests.ListenBrainz;

/// <summary>One recorded request to a ListenBrainz server.</summary>
internal sealed record ListenBrainzRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body)
{
    public string Endpoint => Uri.AbsolutePath[(Uri.AbsolutePath.LastIndexOf('/') + 1)..];

    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string ListenType => Json.GetProperty("listen_type").GetString()!;

    public JsonElement[] Payload => [.. Json.GetProperty("payload").EnumerateArray()];

    public string[] Tracks => [.. Payload.Select(p => p.GetProperty("track_metadata").GetProperty("track_name").GetString()!)];
}

/// <summary>
/// Stands in for a ListenBrainz server. Each endpoint answers from its own queue
/// of canned responses; an empty queue answers with the documented success shape.
/// </summary>
internal sealed class FakeListenBrainzHandler : HttpMessageHandler
{
    public const string ValidBody = """{"code":200,"message":"Token valid.","valid":true,"user_name":"demo-listener"}""";

    private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _responses = [];
    private readonly object _sync = new();

    public List<ListenBrainzRequest> Requests { get; } = [];

    public ListenBrainzRequest[] Calls(string endpoint)
    {
        lock (_sync)
            return [.. Requests.Where(r => r.Endpoint == endpoint)];
    }

    public ListenBrainzRequest[] Submits
    {
        get
        {
            lock (_sync)
                return [.. Requests.Where(r => r.Endpoint == "submit-listens" && r.ListenType != "playing_now")];
        }
    }

    public void Enqueue(string endpoint, HttpStatusCode status, string body, string contentType = "application/json") =>
        Add(endpoint, () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });

    public void EnqueueError(string endpoint, HttpStatusCode status, string error = "synthetic error") =>
        Enqueue(endpoint, status, $$"""{"code":{{(int)status}},"error":"{{error}}"}""");

    public void EnqueueNetworkFailure(string endpoint) =>
        Add(endpoint, () => throw new HttpRequestException("no route to host"));

    private void Add(string endpoint, Func<HttpResponseMessage> response)
    {
        lock (_sync)
        {
            if (!_responses.TryGetValue(endpoint, out var queue))
                _responses[endpoint] = queue = new();
            queue.Enqueue(response);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var recorded = new ListenBrainzRequest(
            request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body);

        Func<HttpResponseMessage>? respond = null;
        lock (_sync)
        {
            Requests.Add(recorded);
            if (_responses.TryGetValue(recorded.Endpoint, out var queue) && queue.Count > 0)
                respond = queue.Dequeue();
        }

        if (respond is not null)
            return respond();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                recorded.Endpoint == "validate-token" ? ValidBody : """{"status":"ok"}""",
                Encoding.UTF8, "application/json"),
        };
    }
}

internal sealed class MemoryTokenStore : IListenBrainzTokenStore
{
    public ListenBrainzConnection? Saved { get; set; }
    public int Clears { get; private set; }

    public ListenBrainzConnection? Load() => Saved;

    public void Save(ListenBrainzConnection connection) => Saved = connection;

    public void Clear()
    {
        Saved = null;
        Clears++;
    }
}
