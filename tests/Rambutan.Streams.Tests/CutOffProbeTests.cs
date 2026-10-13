using System.Net;

namespace Rambutan.Streams.Tests;

public sealed class CutOffProbeTests
{
    private const string Url = "https://rr1.example.invalid/videoplayback?itag=251&clen=3143046&expire=1";

    /// <summary>Answers each request with <paramref name="status"/> and records its range.</summary>
    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<string?> Ranges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Ranges.Add(request.Headers.Range?.ToString());
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.PartialContent, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task Asks_for_the_last_two_bytes_and_only_a_403_means_cut_off(HttpStatusCode status, bool cutOff)
    {
        var handler = new RecordingHandler(status);
        using var http = new HttpClient(handler);

        Assert.Equal(cutOff, await CutOffProbe.IsCutOffAsync(http, Url, CancellationToken.None));
        Assert.Equal(["bytes=3143044-3143045"], handler.Ranges);
    }

    [Fact]
    public async Task A_url_without_its_length_is_not_asked_about()
    {
        var handler = new RecordingHandler(HttpStatusCode.Forbidden);
        using var http = new HttpClient(handler);

        Assert.False(await CutOffProbe.IsCutOffAsync(
            http, "https://rr1.example.invalid/videoplayback?itag=251", CancellationToken.None));
        Assert.Empty(handler.Ranges);
    }

    [Fact]
    public async Task A_network_error_is_not_cut_off()
    {
        using var http = new HttpClient(new FailingHandler());

        Assert.False(await CutOffProbe.IsCutOffAsync(http, Url, CancellationToken.None));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection reset");
    }
}
