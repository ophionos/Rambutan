using System.Net;
using System.Text;

namespace Rambutan.InnerTube.Tests;

/// <summary>Session probing and rejection.</summary>
public class SessionProbeTests
{
    private const string AccountMenu =
        """{"actions":[{"openPopupAction":{"popup":{"multiPageMenuRenderer":{"header":{"activeAccountHeaderRenderer":{"accountName":{"runs":[{"text":"Test Listener"}]}}}}}}}]}""";

    private sealed class Handler(Func<HttpRequestMessage, HttpStatusCode> status) : HttpMessageHandler
    {
        public List<string?> Cookies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Cookies.Add(request.Headers.TryGetValues("Cookie", out var v) ? string.Join("", v) : null);
            var code = status(request);
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(code == HttpStatusCode.OK ? AccountMenu : "{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task ProbingACandidate_UsesItsCookies_AndLeavesTheClientSessionAlone()
    {
        var handler = new Handler(_ => HttpStatusCode.OK);
        using var client = new InnerTubeClient(new HttpClient(handler));
        var live = InnerTubeSession.FromCookieString("SAPISID=live")!;
        client.SetSession(live);

        var info = await client.GetSessionInfoAsync(InnerTubeSession.FromCookieString("SAPISID=candidate")!, default);

        Assert.Equal(new SessionInfo(SessionState.Valid, "Test Listener"), info);
        Assert.Equal("SAPISID=candidate", Assert.Single(handler.Cookies));
        Assert.Same(live, client.Session);
    }

    [Fact]
    public async Task TheAccountPhoto_IsTheLargestSizeSent_AndOptional()
    {
        const string withPhoto =
            """{"actions":[{"openPopupAction":{"popup":{"multiPageMenuRenderer":{"header":{"activeAccountHeaderRenderer":{"accountName":{"runs":[{"text":"Test Listener"}]},"accountPhoto":{"thumbnails":[{"url":"https://yt3.ggpht.com/x=s48","width":48,"height":48},{"url":"https://yt3.ggpht.com/x=s88","width":88,"height":88}]}}}}}}}]}""";
        using var client = new InnerTubeClient(new HttpClient(new Json(withPhoto)));

        var info = await client.GetSessionInfoAsync(InnerTubeSession.FromCookieString("SAPISID=live")!, default);

        Assert.Equal(new SessionInfo(SessionState.Valid, "Test Listener", "https://yt3.ggpht.com/x=s88"), info);

        // Without one, the session is as valid, with no photo.
        using var bare = new InnerTubeClient(new HttpClient(new Handler(_ => HttpStatusCode.OK)));
        var plain = await bare.GetSessionInfoAsync(InnerTubeSession.FromCookieString("SAPISID=live")!, default);
        Assert.Equal(new SessionInfo(SessionState.Valid, "Test Listener"), plain);
    }

    [Fact]
    public async Task TheAccountId_IsItsOwnChannel_FromARecordedMenu()
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "account-menu.json"));
        using var client = new InnerTubeClient(new HttpClient(new Json(body)));

        var info = await client.GetSessionInfoAsync(InnerTubeSession.FromCookieString("SAPISID=live")!, default);

        Assert.Equal(SessionState.Valid, info.State);
        Assert.Equal("Test Listener", info.AccountName);
        Assert.Equal("UCtestListener0000000000", info.AccountId);
        Assert.Equal("https://yt3.ggpht.com/test-avatar=s88", info.AccountPhotoUrl);
    }

    private sealed class Json(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task RejectedCandidate_IsExpired_AndDoesNotRaiseSessionRejected()
    {
        using var client = new InnerTubeClient(new HttpClient(new Handler(_ => HttpStatusCode.Unauthorized)));
        client.SetSession(InnerTubeSession.FromCookieString("SAPISID=live"));
        var raised = 0;
        client.SessionRejected += () => raised++;

        var info = await client.GetSessionInfoAsync(InnerTubeSession.FromCookieString("SAPISID=pasted")!, default);

        Assert.Equal(SessionState.Expired, info.State);
        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    [InlineData(HttpStatusCode.Forbidden, 1)]
    [InlineData(HttpStatusCode.InternalServerError, 0)]
    public async Task LiveSessionRefused_RaisesSessionRejected(HttpStatusCode code, int expected)
    {
        using var client = new InnerTubeClient(new HttpClient(new Handler(_ => code)));
        client.SetSession(InnerTubeSession.FromCookieString("SAPISID=live"));
        var raised = 0;
        client.SessionRejected += () => raised++;

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLibraryPlaylistsAsync());

        Assert.Equal(expected, raised);
    }

    [Fact]
    public async Task AnonymousRequestRefused_DoesNotRaiseSessionRejected()
    {
        using var client = new InnerTubeClient(new HttpClient(new Handler(_ => HttpStatusCode.Forbidden)));
        var raised = 0;
        client.SessionRejected += () => raised++;

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetHomeAsync());

        Assert.Equal(0, raised);
    }
}
