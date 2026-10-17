using System.Net;
using System.Text;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// Renewing a session YouTube no longer accepts: the Google sign-in behind it
/// has Google issue new youtube.com cookies through its sign-in redirects, as
/// recorded live on 2026-10-16.
/// </summary>
public class SessionReissueTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private const string Google = "SID=g; LSID=l; __Host-GAPS=p; SAPISID=s";

    [Theory]
    [InlineData("SID=g; LSID=l")]
    [InlineData("SID=g; __Host-1PLSID=l")]
    [InlineData("curl 'https://accounts.google.com/' -H 'cookie: SID=g; __Host-3PLSID=l; SAPISID=s'")]
    public void AnAccountsGoogleCopy_IsAGoogleSignIn(string copied) =>
        Assert.NotNull(GoogleSignIn.FromCookieString(copied));

    [Theory]
    [InlineData("SID=a; SAPISID=b; LOGIN_INFO=c")] // a music.youtube.com request
    [InlineData("LSID=l")]
    [InlineData("")]
    public void AnythingElse_IsNot(string copied) =>
        Assert.Null(GoogleSignIn.FromCookieString(copied));

    [Fact]
    public void GoogleCookies_TakeWhatAccountsGoogleWouldReceive()
    {
        var google = GoogleSignIn.FromCookieString("SID=g; LSID=l")!;

        var updated = google.WithSetCookies(
            ["SIDCC=new; Domain=.google.com", "__Host-GAPS=host; Path=/", "YSC=no; Domain=.youtube.com"],
            "accounts.google.com", Now);

        Assert.Equal("SID=g; LSID=l; SIDCC=new; __Host-GAPS=host", updated.CookieHeader);
        Assert.Same(updated, updated.WithSetCookies(["SIDCC=new; Domain=.google.com"], "accounts.google.com", Now));
        Assert.Same(google, google.WithSetCookies(["OTHER=1; Path=/"], "www.youtube.com", Now));
    }

    private sealed class Server(Func<Uri, (HttpStatusCode Status, string? Location, string[] Cookies)> answer)
        : HttpMessageHandler
    {
        public List<(Uri Uri, string? Cookie)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!,
                request.Headers.TryGetValues("Cookie", out var c) ? string.Concat(c) : null));
            var (status, location, cookies) = answer(request.RequestUri!);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("<html></html>", Encoding.UTF8, "text/html"),
                RequestMessage = request,
            };
            if (location is not null)
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            foreach (var cookie in cookies)
                response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
            return Task.FromResult(response);
        }
    }

    // The chain as Google answered it live, with made-up values.
    private static (HttpStatusCode, string?, string[]) Live(Uri uri) => (uri.Host, uri.AbsolutePath) switch
    {
        ("accounts.google.com", "/ServiceLogin") => (HttpStatusCode.Found,
            "https://accounts.youtube.com/accounts/SetSID?ssdc=1&sidt=t",
            ["SIDCC=g2; Domain=.google.com; Path=/", "__Secure-1PSIDCC=g2; Domain=.google.com; Path=/"]),
        ("accounts.youtube.com", "/accounts/SetSID") => (HttpStatusCode.Found,
            "https://www.youtube.com/signin?action_handle_signin=true",
            ["SID=y; Domain=.youtube.com", "SAPISID=y; Domain=.youtube.com", "__Secure-1PSIDTS=y; Domain=.youtube.com"]),
        ("www.youtube.com", "/signin") => (HttpStatusCode.SeeOther, "https://music.youtube.com/",
            ["LOGIN_INFO=y; Domain=.youtube.com", "VISITOR_INFO1_LIVE=; Domain=.youtube.com; Max-Age=0"]),
        _ => (HttpStatusCode.NotFound, null, []),
    };

    private static InnerTubeClient Client(Server server, out InnerTubeSession session)
    {
        var client = new InnerTubeClient(new HttpClient(server));
        session = InnerTubeSession.FromCookieString("SAPISID=dead; __Secure-1PSIDTS=stale; PREF=f")!;
        client.SetSession(session);
        client.SetGoogleSignIn(GoogleSignIn.FromCookieString(Google));
        return client;
    }

    [Fact]
    public async Task GoogleIssuesNewCookies_WhichBecomeTheSameSignInsSession()
    {
        var server = new Server(Live);
        using var client = Client(server, out var session);
        InnerTubeSession? reported = null;
        GoogleSignIn? google = null;
        client.SessionUpdated += s => reported = s;
        client.GoogleSignInUpdated += g => google = g;

        Assert.True(await client.ReissueSessionAsync());

        // Only what Google set: the dead cookies are gone, the deleted one isn't added.
        Assert.Equal("SID=y; SAPISID=y; __Secure-1PSIDTS=y; LOGIN_INFO=y", client.Session!.CookieHeader);
        Assert.Equal("y", client.Session.Sapisid);
        Assert.Same(reported, client.Session);
        Assert.True(InnerTubeSession.IsSameSignIn(session, client.Session));
        Assert.Same(google, client.GoogleSignIn);
        Assert.Equal(Google + "; SIDCC=g2; __Secure-1PSIDCC=g2", client.GoogleSignIn!.CookieHeader);
    }

    [Fact]
    public async Task TheGoogleCookies_GoOnlyToAccountsGoogle_AndMusicIsntFetched()
    {
        var server = new Server(Live);
        using var client = Client(server, out _);

        await client.ReissueSessionAsync();

        Assert.Equal(
            [("accounts.google.com", Google), ("accounts.youtube.com", null), ("www.youtube.com", null)],
            server.Requests.Select(r => (r.Uri.Host, r.Cookie)));
        var start = server.Requests[0].Uri;
        Assert.Equal("/ServiceLogin", start.AbsolutePath);
        Assert.Contains("service=youtube", start.Query);
        Assert.Contains("passive=true", start.Query);
    }

    [Fact]
    public async Task SignedOut_GoogleAsksForASignIn_AndTheSessionStays()
    {
        var server = new Server(uri => uri.AbsolutePath == "/ServiceLogin"
            ? (HttpStatusCode.Found, "/v3/signin/identifier?flowName=x", [])
            : (HttpStatusCode.OK, null, []));
        using var client = Client(server, out var session);

        Assert.False(await client.ReissueSessionAsync());

        Assert.Same(session, client.Session);
        Assert.Equal("/v3/signin/identifier", server.Requests[1].Uri.AbsolutePath);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task ARedirectAwayFromGooglesSignIn_IsNotFollowed()
    {
        var server = new Server(uri => uri.Host == "accounts.google.com"
            ? (HttpStatusCode.Found, "https://example.com/elsewhere", [])
            : (HttpStatusCode.OK, null, []));
        using var client = Client(server, out var session);

        Assert.False(await client.ReissueSessionAsync());

        Assert.Single(server.Requests);
        Assert.Same(session, client.Session);
    }

    [Fact]
    public async Task ReachingMusic_WithoutASapisid_IsNoSession()
    {
        var server = new Server(uri => uri.Host == "accounts.google.com"
            ? (HttpStatusCode.Found, "https://music.youtube.com/", ["YSC=1; Domain=.youtube.com"])
            : (HttpStatusCode.OK, null, []));
        using var client = Client(server, out var session);

        Assert.False(await client.ReissueSessionAsync());
        Assert.Same(session, client.Session);
    }

    [Fact]
    public async Task ASignOutWhileGoogleAnswers_IsNotUndone()
    {
        InnerTubeClient? client = null;
        var server = new Server(uri =>
        {
            if (uri.Host == "www.youtube.com")
                client!.SetSession(null);
            return Live(uri);
        });
        client = Client(server, out _);
        var raised = 0;
        client.SessionUpdated += _ => raised++;

        Assert.False(await client.ReissueSessionAsync());

        Assert.Null(client.Session);
        Assert.Equal(0, raised);
        client.Dispose();
    }

    [Fact]
    public async Task WithoutAGoogleSignIn_NothingIsAsked()
    {
        var server = new Server(Live);
        using var client = Client(server, out _);
        client.SetGoogleSignIn(null);

        Assert.False(await client.ReissueSessionAsync());
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Offline_IsNoSession()
    {
        using var client = new InnerTubeClient(new HttpClient(new Offline()));
        var session = InnerTubeSession.FromCookieString("SAPISID=dead")!;
        client.SetSession(session);
        client.SetGoogleSignIn(GoogleSignIn.FromCookieString(Google));

        Assert.False(await client.ReissueSessionAsync());
        Assert.Same(session, client.Session);
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("offline");
    }
}
