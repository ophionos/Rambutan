using System.Net;
using System.Text;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// Keeping a stored session fresh: Google rotates some of its cookies and
/// soon rejects the old values, so the client merges what responses set and can
/// ask for a rotation.
/// </summary>
public class SessionRotationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static InnerTubeSession Session(string cookies = "SAPISID=a; __Secure-1PSIDTS=old; SIDCC=x") =>
        InnerTubeSession.FromCookieString(cookies)!;

    [Fact]
    public void CookiesAnotherProgramEndedWith_AreMerged_ForTheSameSignInOnly()
    {
        var client = new InnerTubeClient();
        var session = Session();
        client.SetSession(session);
        InnerTubeSession? reported = null;
        client.SessionUpdated += s => reported = s;

        Assert.False(client.ApplyCookies(session, []));
        Assert.True(client.ApplyCookies(session, [("SIDCC", "y"), ("YSC", "1")]));

        Assert.Equal("SAPISID=a; __Secure-1PSIDTS=old; SIDCC=y; YSC=1", client.Session!.CookieHeader);
        Assert.Same(client.Session, reported);
        Assert.False(client.ApplyCookies(Session(), [("SIDCC", "z")]));
    }

    [Fact]
    public void NewValues_ReplaceOldOnes_InPlace()
    {
        var session = Session();

        var updated = session.WithSetCookies(
            ["__Secure-1PSIDTS=new; Domain=.youtube.com; Path=/; Secure; HttpOnly"], "accounts.youtube.com", Now)!;

        Assert.Equal("SAPISID=a; __Secure-1PSIDTS=new; SIDCC=x", updated.CookieHeader);
        Assert.True(InnerTubeSession.IsSameSignIn(session, updated));
    }

    [Fact]
    public void NothingChanged_ReturnsTheSameInstance()
    {
        var session = Session();

        Assert.Same(session, session.WithSetCookies(["SIDCC=x; Domain=.youtube.com"], "music.youtube.com", Now));
        Assert.Same(session, session.WithSetCookies([], "music.youtube.com", Now));
    }

    [Theory]
    [InlineData("OTHER=1; Domain=.google.com", "accounts.youtube.com")]
    [InlineData("OTHER=1; Path=/", "accounts.youtube.com")] // host-only on another host
    [InlineData("malformed", "music.youtube.com")]
    public void CookiesAMusicRequestWouldNotCarry_AreIgnored(string header, string host)
    {
        var session = Session();

        Assert.Same(session, session.WithSetCookies([header], host, Now));
    }

    [Fact]
    public void HostOnlyCookies_FromMusicItself_AreKept()
    {
        var updated = Session().WithSetCookies(["YSC=1; Path=/"], "music.youtube.com", Now)!;

        Assert.EndsWith("; YSC=1", updated.CookieHeader);
    }

    [Theory]
    [InlineData("SIDCC=; Domain=.youtube.com; Max-Age=0")]
    [InlineData("SIDCC=gone; Domain=.youtube.com; Expires=Thu, 04-Jan-2024 23:19:11 GMT")]
    public void ExpiredCookies_AreRemoved_AndUnknownOnesIgnored(string header)
    {
        var session = Session();

        Assert.Equal("SAPISID=a; __Secure-1PSIDTS=old", session.WithSetCookies([header], "music.youtube.com", Now)!.CookieHeader);
        var deleteUnknown = header.Replace("SIDCC", "VISITOR_INFO1_LIVE");
        Assert.Same(session, session.WithSetCookies([deleteUnknown], "music.youtube.com", Now));
    }

    [Fact]
    public void AFutureExpiry_IsAnUpdate_NotADeletion()
    {
        var updated = Session().WithSetCookies(
            ["SIDCC=y; expires=Thu, 30-Sep-2027 23:22:35 GMT; domain=.youtube.com"], "accounts.youtube.com", Now)!;

        Assert.Contains("SIDCC=y", updated.CookieHeader);
    }

    [Fact]
    public void ARotatedSapisid_ChangesTheAuthorizationKey()
    {
        var updated = Session().WithSetCookies(["SAPISID=b; Domain=.youtube.com"], "music.youtube.com", Now)!;

        Assert.Equal("b", updated.Sapisid);
    }

    [Fact]
    public void RemovingTheSapisid_LeavesNoSession() =>
        Assert.Null(Session().WithSetCookies(["SAPISID=; Domain=.youtube.com; Max-Age=0"], "music.youtube.com", Now));

    [Fact]
    public void APastedCopyOfTheSameCookies_IsANewSignIn() =>
        Assert.False(InnerTubeSession.IsSameSignIn(Session(), Session()));

    private sealed class Server(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Cookie, string Body, string? Origin)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!,
                request.Headers.TryGetValues("Cookie", out var c) ? string.Join("", c) : null, body,
                request.Headers.TryGetValues("Origin", out var o) ? string.Join("", o) : null));
            var response = answer(request);
            response.RequestMessage = request;
            return response;
        }
    }

    private static HttpResponseMessage Answer(HttpStatusCode code, params string[] setCookies)
    {
        var response = new HttpResponseMessage(code)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        foreach (var cookie in setCookies)
            response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
        return response;
    }

    [Fact]
    public async Task Rotate_PostsToGoogle_AndMakesTheFreshCookiesTheSession()
    {
        var server = new Server(_ => Answer(HttpStatusCode.OK,
            "__Secure-1PSIDTS=new; Domain=.youtube.com; Expires=Thu, 30-Sep-2027 23:22:35 GMT"));
        using var client = new InnerTubeClient(new HttpClient(server));
        var session = Session();
        client.SetSession(session);
        InnerTubeSession? reported = null;
        client.SessionUpdated += s => reported = s;

        Assert.True(await client.RotateCookiesAsync());

        var (uri, cookie, body, origin) = Assert.Single(server.Requests);
        Assert.Equal("https://accounts.youtube.com/RotateCookies", uri.ToString());
        Assert.Equal(session.CookieHeader, cookie);
        Assert.Equal("""[000,"-0000000000000000000"]""", body);
        Assert.Equal("https://music.youtube.com", origin);
        Assert.Same(reported, client.Session);
        Assert.Contains("__Secure-1PSIDTS=new", client.Session!.CookieHeader);
    }

    [Fact]
    public async Task ARefusedRotation_LeavesTheSessionAlone()
    {
        using var client = new InnerTubeClient(new HttpClient(new Server(_ => Answer(HttpStatusCode.Unauthorized))));
        var session = Session();
        client.SetSession(session);

        Assert.False(await client.RotateCookiesAsync());
        Assert.Same(session, client.Session);
    }

    [Fact]
    public async Task ApiResponsesThatSetCookies_UpdateTheSession()
    {
        using var client = new InnerTubeClient(new HttpClient(new Server(_ =>
            Answer(HttpStatusCode.OK, "SIDCC=fresh; Domain=.youtube.com"))));
        client.SetSession(Session());
        var raised = 0;
        client.SessionUpdated += _ => raised++;

        try { await client.GetHomeAsync(); } catch (Exception) { /* "{}" isn't a Home page; the headers are what count */ }

        Assert.Contains("SIDCC=fresh", client.Session!.CookieHeader);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task AnAnswerThatArrivesAfterSignOut_DoesNotBringTheSessionBack()
    {
        InnerTubeClient? client = null;
        var server = new Server(_ =>
        {
            client!.SetSession(null); // signed out while the request was out
            return Answer(HttpStatusCode.OK, "__Secure-1PSIDTS=new; Domain=.youtube.com");
        });
        client = new InnerTubeClient(new HttpClient(server));
        client.SetSession(Session());
        var raised = 0;
        client.SessionUpdated += _ => raised++;

        await client.RotateCookiesAsync();

        Assert.Null(client.Session);
        Assert.Equal(0, raised);
        client.Dispose();
    }

    [Fact]
    public async Task ARefusalOfAnOlderCopy_StillCountsAsTheSessionsRefusal()
    {
        // The request went out with the pre-rotation copy; the refusal is still
        // about this sign-in, so the app must re-check it.
        InnerTubeClient? client = null;
        var server = new Server(_ =>
        {
            client!.SetSession(client.Session!.WithSetCookies(["SIDCC=y; Domain=.youtube.com"], "music.youtube.com", Now));
            return Answer(HttpStatusCode.Unauthorized);
        });
        client = new InnerTubeClient(new HttpClient(server));
        client.SetSession(Session());
        var rejected = 0;
        client.SessionRejected += () => rejected++;

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLibraryPlaylistsAsync());

        Assert.Equal(1, rejected);
        client.Dispose();
    }
}
