using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;

namespace Rambutan.App.Services.Demo;

/// <summary>
/// Answers Spotify's embed page in demo mode, so the Spotify import reads a
/// link end to end through the real reader. Every link is the same short
/// list, of songs the demo's search can find and one it can't; the list is
/// shorter than the page's limit, so the web player's API is never asked.
/// <c>RAMBUTAN_DEMO_FAIL=spotify</c> (or <c>all</c>) fails it as if offline;
/// <c>spotify-unavailable</c> (named only) answers as Spotify does for a
/// private or removed list: its "Page not found".
/// </summary>
public sealed class DemoSpotifyHandler(DemoOptions options) : HttpMessageHandler
{
    public const string Area = "spotify";
    public const string UnavailableArea = "spotify-unavailable";

    private const string Page = """
        <!DOCTYPE html><html><head><title>Spotify Embed</title></head><body>
        <script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"state":{"data":{"entity":{
          "type":"playlist","name":"Demo Road Trip","trackList":[
            {"uri":"spotify:track:0DiWol3AO6WpXZgp0goxAV","title":"One More Time","subtitle":"Daft Punk","duration":320357,"entityType":"track"},
            {"uri":"spotify:track:2cGxRwrMyEAp8dEbuZaVv6","title":"Instant Crush","subtitle":"Daft Punk, Julian Casablancas","duration":337560,"entityType":"track"},
            {"uri":"spotify:track:5W3cjX2J3tjhG8zb6u0qHn","title":"Harder, Better, Faster, Stronger","subtitle":"Daft Punk","duration":224693,"entityType":"track"},
            {"uri":"spotify:episode:5Xt5DXGzch68nYYamXrNxZ","title":"A podcast episode","subtitle":"A show","entityType":"episode"},
            {"uri":"spotify:track:7bxaFZ1O3cHkgLKMsdC3xR","title":"Nothing Here","subtitle":"Nobody Known","duration":200000,"entityType":"track"}
          ]}},"settings":{"session":{"accessToken":"demo","isAnonymous":true}}}}}}</script>
        </body></html>
        """;

    private const string NotFound = """
        <!DOCTYPE html><html><head><title>Page not found</title></head><body>
        <script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"status":404,"title":"Page not found"}}}</script>
        </body></html>
        """;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        AppLog.Write("demo", $"spotify {request.RequestUri?.AbsolutePath} → {Area}");

        await Task.Delay(options.Latency, ct);

        if (options.Fails(Area))
            throw new HttpRequestException($"Demo: '{Area}' is set to fail (RAMBUTAN_DEMO_FAIL).");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(options.FailingAreas.Contains(UnavailableArea) ? NotFound : Page,
                Encoding.UTF8, "text/html"),
        };
    }
}
