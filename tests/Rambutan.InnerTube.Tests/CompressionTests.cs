using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Rambutan.InnerTube.Tests;

/// <summary>.NET sends no Accept-Encoding unless the handler is told to
/// decompress, and YouTube Music then answers every page uncompressed.</summary>
public class CompressionTests
{
    [Fact]
    public async Task TheClientsTransport_AsksForCompressedAnswers_AndReadsThem()
    {
        const string json = """{"contents":{"text":"Give Life Back to Music"}}""";
        using var body = new MemoryStream();
        using (var gzip = new GZipStream(body, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(json));
        var compressed = body.ToArray();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var ct = TestContext.Current.CancellationToken;
        var served = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            var head = new StringBuilder();
            var buffer = new byte[4096];
            while (!head.ToString().Contains("\r\n\r\n"))
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                    break;
                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: gzip\r\n" +
                $"Content-Length: {compressed.Length}\r\nConnection: close\r\n\r\n"), ct);
            await stream.WriteAsync(compressed, ct);
            return head.ToString();
        }, ct);

        try
        {
            using var http = new HttpClient(InnerTubeClient.NewTransport());
            var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            using var response = await http.GetAsync(url, ct);

            Assert.Equal(json, await response.Content.ReadAsStringAsync(ct));
            var accepted = (await served).Split("\r\n")
                .Single(line => line.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("gzip", accepted);
            Assert.Contains("br", accepted);
        }
        finally
        {
            listener.Stop();
        }
    }
}
