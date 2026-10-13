using System.Net;
using System.Text;
using System.Text.Json;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// Captures each request the client sends (endpoint, query string, JSON body) and
/// answers with <see cref="Response"/>, so tests can pin request payloads without
/// the network.
/// </summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    public sealed record Sent(string Endpoint, string Query, JsonElement Body);

    public List<Sent> Requests { get; } = [];

    /// <summary>The body answered for a request; defaults to a successful edit.</summary>
    public Func<Sent, string> Response { get; set; } = _ => """{"status":"STATUS_SUCCEEDED"}""";

    public static (InnerTubeClient Client, RecordingHandler Handler) Client(bool signedIn = true)
    {
        var handler = new RecordingHandler();
        var client = new InnerTubeClient(new HttpClient(handler));
        if (signedIn)
            client.SetSession(InnerTubeSession.FromCookieString("SAPISID=test"));
        return (client, handler);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var endpoint = path[(path.IndexOf("/v1/", StringComparison.Ordinal) + 4)..];
        var json = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
        var sent = new Sent(endpoint, request.RequestUri.Query, JsonDocument.Parse(json).RootElement.Clone());
        Requests.Add(sent);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Response(sent), Encoding.UTF8, "application/json"),
        };
    }
}
