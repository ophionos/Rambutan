namespace Rambutan.Streams;

public enum Connectivity
{
    Online,
    /// <summary>The connection itself failed: no network, no DNS, a dead proxy.</summary>
    Offline,
    /// <summary>No answer in time. A slow network looks like this too, so it is
    /// never enough on its own to call the network gone.</summary>
    Unknown,
}

/// <summary>
/// Asks whether YouTube Music can be reached at all, with one small request to
/// its <c>generate_204</c> endpoint. A failed resolution takes yt-dlp most of 20 s
/// to report (it retries); this answers in milliseconds when the machine has no
/// network, which is the case that matters.
/// </summary>
public sealed class ConnectivityProbe(HttpClient http, TimeSpan? timeout = null)
{
    private static readonly Uri Target = new("https://music.youtube.com/generate_204");

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(5);

    public async Task<Connectivity> CheckAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Target);
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            // Any answer at all, even an error status, means the network is there.
            return Connectivity.Online;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Connectivity.Unknown;
        }
        catch (HttpRequestException)
        {
            return Connectivity.Offline;
        }
    }
}
