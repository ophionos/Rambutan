using System.Net;
using System.Text;
using Rambutan.Core.Diagnostics;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// What the client logs about the session: enough to tell why a sign-in expired,
/// and never a cookie value. AppLog's sink is process-wide, and other classes'
/// probes can log through it while a case here holds it, so each case picks out
/// its own line instead of expecting to be alone.
/// </summary>
public class SessionLogTests
{
    private sealed class Handler(string body, params string[] setCookies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            foreach (var cookie in setCookies)
                response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
            return Task.FromResult(response);
        }
    }

    private static async Task<List<string>> CaptureAsync(Func<Task> act)
    {
        var lines = new List<string>();
        var previous = AppLog.Sink;
        AppLog.Sink = (category, message) =>
        {
            if (category == "session")
                lock (lines) lines.Add(message);
        };
        try
        {
            await act();
        }
        finally
        {
            AppLog.Sink = previous;
        }
        return lines;
    }

    [Fact]
    public async Task SignedOutAnswer_LogsExpiredWithTheResponseKeys()
    {
        using var client = new InnerTubeClient(new HttpClient(new Handler("""{"responseContext":{},"actions":[]}""")));
        client.SetSession(InnerTubeSession.FromCookieString("SAPISID=secret-value"));

        var lines = await CaptureAsync(() => client.GetSessionInfoAsync());

        var line = Assert.Single(lines, l => l.Contains("responseContext, actions"));
        Assert.Contains("expired", line);
        Assert.All(lines, l => Assert.DoesNotContain("secret-value", l));
    }

    [Fact]
    public async Task SetCookieNames_AreLoggedOnce_AndValuesNever()
    {
        using var client = new InnerTubeClient(new HttpClient(new Handler(
            "{}",
            "SIDCC=rotated-secret; Path=/; Domain=.youtube.com; Secure",
            "__Secure-3PSIDCC=other-secret; Path=/; Secure; HttpOnly")));
        client.SetSession(InnerTubeSession.FromCookieString("SAPISID=test"));

        var lines = await CaptureAsync(async () =>
        {
            await client.GetHomeAsync();
            await client.GetHomeAsync();
        });

        var line = Assert.Single(lines, l => l.Contains("SIDCC, __Secure-3PSIDCC"));
        Assert.Contains("sets cookies", line);
        Assert.All(lines, l => Assert.DoesNotContain("secret", l));
    }
}
