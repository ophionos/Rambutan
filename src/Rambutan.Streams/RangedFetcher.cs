using System.Net;
using System.Net.Http.Headers;

namespace Rambutan.Streams;

/// <summary>
/// Downloads a googlevideo URL in ranged chunks. Googlevideo throttles unranged
/// GETs to ~2× the media bitrate; ranged chunks download at full speed (measured
/// 36 KB/s vs 13 MB/s). Shared by the cache fill and downloads.
/// </summary>
internal sealed class RangedFetcher(HttpClient http)
{
    private const int ChunkSize = 1 << 20;
    private const long MaxFileBytes = 256 << 20; // sanity guard, ~50× a typical track

    // HttpClient.Timeout covers headers only for streamed bodies; without a chunk
    // deadline a stalled read wedges the fetch forever.
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Ranged-chunk download into <paramref name="target"/>; returns the
    /// total length to commit, or null to discard the partial file.</summary>
    /// <param name="progress">Called after each chunk with (bytes written, total).</param>
    public async Task<long?> FetchAsync(
        string url, Stream target, Action<long, long>? progress, CancellationToken ct)
    {
        long? total = null;
        long written = 0;
        while (total is null || written < total)
        {
            using var chunkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            chunkCts.CancelAfter(ChunkTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(written, written + ChunkSize - 1);

            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, chunkCts.Token);
            if (response.StatusCode != HttpStatusCode.PartialContent)
                return null;

            total ??= response.Content.Headers.ContentRange?.Length;
            if (total is null or > MaxFileBytes)
                return null;

            var before = written;
            await using var content = await response.Content.ReadAsStreamAsync(chunkCts.Token);
            await content.CopyToAsync(target, chunkCts.Token);
            written = target.Length;
            if (written == before)
                return null; // server stopped serving — never store a short file
            progress?.Invoke(written, total.Value);
        }

        return total;
    }
}
