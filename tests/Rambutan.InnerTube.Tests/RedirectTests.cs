using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Rambutan.InnerTube.Tests;

/// <summary>The session cookies are a header the client sets by hand, which
/// .NET's own redirect handling would carry to whatever host a redirect names.
/// So the client follows none.</summary>
public class RedirectTests
{
    /// <summary>A loopback server that gives every request the same answer and
    /// keeps what it was sent.</summary>
    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _answer;

        public Server(string answer)
        {
            _answer = answer;
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public List<string> Requests { get; } = [];

        public Uri Url => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var head = new StringBuilder();
                    var buffer = new byte[4096];
                    while (!head.ToString().Contains("\r\n\r\n"))
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0)
                            break;
                        head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }
                    lock (Requests)
                        Requests.Add(head.ToString());
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(_answer), _stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    [Fact]
    public async Task TheClientsTransport_FollowsNoRedirect_SoNoCookieReachesAnotherHost()
    {
        using var other = new Server("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var first = new Server(
            $"HTTP/1.1 302 Found\r\nLocation: {other.Url}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var http = new HttpClient(InnerTubeClient.NewTransport());
        using var request = new HttpRequestMessage(HttpMethod.Post, first.Url) { Content = new StringContent("{}") };
        request.Headers.TryAddWithoutValidation("Cookie", "SAPISID=secret");

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        lock (first.Requests)
            Assert.Contains("SAPISID=secret", Assert.Single(first.Requests));
        lock (other.Requests)
            Assert.Empty(other.Requests);
    }

    private sealed class Redirecting : HttpMessageHandler
    {
        public int Sent;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent++;
            var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("") };
            response.Headers.Location = new Uri("https://elsewhere.example/youtubei/v1/browse");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ARedirectFromAnApiCall_IsAFailure_ThatDoesNotEndTheSession()
    {
        var handler = new Redirecting();
        using var client = new InnerTubeClient(new HttpClient(handler));
        var session = InnerTubeSession.FromCookieString("SAPISID=test")!;
        client.SetSession(session);
        var rejected = false;
        client.SessionRejected += () => rejected = true;

        var info = await client.GetSessionInfoAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Unreachable, info.State);
        Assert.Equal(1, handler.Sent);
        Assert.False(rejected);
        Assert.Same(session, client.Session);
    }
}
