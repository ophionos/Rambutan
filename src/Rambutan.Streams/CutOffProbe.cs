using System.Net;
using System.Net.Http.Headers;
using System.Web;

namespace Rambutan.Streams;

/// <summary>
/// Googlevideo serves some fresh URLs only up to about their first megabyte and
/// answers 403 past it, for every format of that resolution. Asking for the
/// file's last two bytes tells such a URL apart before anything plays it. Any other
/// outcome (no length in the URL, a network error, a slow answer) counts as not
/// cut off: the play path still retries an open that fails.
/// </summary>
internal static class CutOffProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<bool> IsCutOffAsync(HttpClient http, string url, CancellationToken ct)
    {
        if (ContentLength(url) is not { } length || length < 2)
            return false;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(length - 2, length - 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.StatusCode == HttpStatusCode.Forbidden;
        }
        catch (Exception ex) when (ex is HttpRequestException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }

    /// <summary>Googlevideo URLs carry the file's size in the `clen` query parameter.</summary>
    internal static long? ContentLength(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        long.TryParse(HttpUtility.ParseQueryString(uri.Query)["clen"], out var length)
            ? length
            : null;
}
