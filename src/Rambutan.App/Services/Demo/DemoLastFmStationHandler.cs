using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;

namespace Rambutan.App.Services.Demo;

/// <summary>
/// Answers Last.fm's station endpoint in demo mode, so Home's station shelves
/// and artists' stations run end to end through the real client,
/// resolver and matcher. The
/// tracks are ones the demo's song search can find (it always returns the
/// recorded Daft Punk results), plus one it can't, which plays from its Last.fm
/// link, and one with no link at all, which the shelf leaves out.
/// <c>RAMBUTAN_DEMO_FAIL=lastfm</c> (or <c>all</c>) fails it as if offline;
/// <c>lastfm-unavailable</c> (named only) answers as Last.fm does for a profile
/// it has no station for: HTTP 500 with an HTML page.
/// </summary>
public sealed class DemoLastFmStationHandler(DemoOptions options) : HttpMessageHandler
{
    public const string Area = "lastfm";
    public const string UnavailableArea = "lastfm-unavailable";

    private const string Mix = """
        {"playlist": [
          {"name": "Instant Crush", "duration": 338, "artists": [{"name": "Daft Punk"}], "playlinks": [{"affiliate": "youtube", "id": "a5uQMwRMHcs"}]},
          {"name": "One More Time", "duration": 320, "artists": [{"name": "Daft Punk"}], "playlinks": [{"affiliate": "youtube", "id": "FGBhQbmPwH8"}]},
          {"name": "Okay", "duration": 236, "artists": [{"name": "Holy Ghost!"}], "playlinks": [{"affiliate": "youtube", "id": "dQw4w9WgXcQ"}]},
          {"name": "The Grid", "duration": 97, "artists": [{"name": "Daft Punk"}], "playlinks": [{"affiliate": "youtube", "id": "zM5hrt7RAQw"}]},
          {"name": "Nothing Here", "duration": 200, "artists": [{"name": "Nobody Known"}], "playlinks": null}
        ]}
        """;

    private const string Recommended = """
        {"playlist": [
          {"name": "The Grid", "duration": 97, "artists": [{"name": "Daft Punk"}], "playlinks": [{"affiliate": "youtube", "id": "zM5hrt7RAQw"}]},
          {"name": "Instant Crush", "duration": null, "artists": [{"name": "Daft Punk"}], "playlinks": [{"affiliate": "youtube", "id": "a5uQMwRMHcs"}]}
        ]}
        """;

    // An artist's station: the same answer for every artist.
    private const string Similar = """
        {"playlist": [
          {"name": "Harder, Better, Faster, Stronger", "duration": 224, "artists": [{"name": "Daft Punk"}], "playlinks": [{"affiliate": "youtube", "id": "gAjR4_CbPpQ"}]},
          {"name": "Places to Be", "duration": 226, "artists": [{"name": "Fred again.."}], "playlinks": [{"affiliate": "youtube", "id": "0bVFEOb39vk"}]},
          {"name": "One More Time", "duration": 320, "artists": [{"name": "Daft Punk"}], "playlinks": [{"affiliate": "youtube", "id": "FGBhQbmPwH8"}]},
          {"name": "Positif", "duration": 173, "artists": [{"name": "Mr. Oizo"}], "playlinks": null},
          {"name": "Instant Crush", "duration": 338, "artists": [{"name": "Daft Punk"}], "playlinks": [{"affiliate": "youtube", "id": "a5uQMwRMHcs"}]}
        ]}
        """;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        var station = path[(path.LastIndexOf('/') + 1)..];
        AppLog.Write("demo", $"last.fm station/{station} → {Area}");

        await Task.Delay(options.Latency, ct);

        if (options.Fails(Area))
            throw new HttpRequestException($"Demo: '{Area}' is set to fail (RAMBUTAN_DEMO_FAIL).");
        if (options.FailingAreas.Contains(UnavailableArea))
            return Answer(HttpStatusCode.InternalServerError, "<!doctype html><title>Server Error | Last.fm</title>", "text/html");

        return Answer(HttpStatusCode.OK, station switch
        {
            "mix" => Mix,
            "recommended" => Recommended,
            "+similar" => Similar,
            _ => """{"playlist": []}""",
        }, "application/json");
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body, string type) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, type),
    };
}
