namespace Rambutan.InnerTube.Tests;

/// <summary>
/// The sign-in dialog accepts whatever dev tools hands over. These are the shapes
/// the guided steps point at (browsers' "Copy as cURL"), plus the older ones.
/// </summary>
public class CookieTextTests
{
    private const string Cookies = "VISITOR_INFO1_LIVE=abc; SAPISID=s1/x; __Secure-3PAPISID=s3/y; PREF=f6=40000000&tz=Europe.Lisbon";

    [Fact]
    public void BareValue_IsReturnedTrimmed()
    {
        Assert.Equal(Cookies, CookieText.ExtractCookieHeader("  " + Cookies + "\n"));
    }

    [Theory]
    [InlineData("Cookie: ")]
    [InlineData("cookie: ")]
    [InlineData("COOKIE:")]
    public void HeaderLine_DropsTheName(string prefix)
    {
        Assert.Equal(Cookies, CookieText.ExtractCookieHeader(prefix + Cookies));
    }

    [Fact]
    public void HeaderBlock_FindsTheCookieLine()
    {
        // Firefox "Copy Request Headers", or Chrome's raw request headers view.
        var block = string.Join("\r\n",
            "POST /youtubei/v1/browse?prettyPrint=false HTTP/2",
            "Host: music.youtube.com",
            "User-Agent: Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0",
            "Authorization: SAPISIDHASH 1_abc",
            "Cookie: " + Cookies,
            "X-Origin: https://music.youtube.com");

        Assert.Equal(Cookies, CookieText.ExtractCookieHeader(block));
    }

    [Fact]
    public void ChromeCurlBash_ReadsTheDashBArgument()
    {
        var curl =
            "curl 'https://music.youtube.com/youtubei/v1/browse?prettyPrint=false' \\\n" +
            "  -H 'accept: */*' \\\n" +
            "  -H 'authorization: SAPISIDHASH 1_abc' \\\n" +
            "  -b '" + Cookies + "' \\\n" +
            "  -H 'origin: https://music.youtube.com' \\\n" +
            "  --data-raw '{\"context\":{}}'";

        Assert.Equal(Cookies, CookieText.ExtractCookieHeader(curl));
    }

    [Fact]
    public void CurlHeaderForm_ReadsTheCookieHeader()
    {
        // Firefox "Copy as cURL (POSIX)", and older Chrome builds.
        var curl =
            "curl 'https://music.youtube.com/youtubei/v1/browse?prettyPrint=false' --compressed -X POST " +
            "-H 'Accept: */*' -H 'Cookie: " + Cookies + "' -H 'TE: trailers'";

        Assert.Equal(Cookies, CookieText.ExtractCookieHeader(curl));
    }

    [Fact]
    public void CurlAnsiCQuoting_IsUnescaped()
    {
        // Chrome switches to $'…' when the value holds a quote or backslash.
        var curl = "curl 'https://music.youtube.com/' -b $'A=it\\'s; SAPISID=x'";

        Assert.Equal("A=it's; SAPISID=x", CookieText.ExtractCookieHeader(curl));
    }

    [Fact]
    public void ChromeCurlCmd_RemovesTheCarets()
    {
        // "Copy as cURL (cmd)" on Windows: ^" quotes, ^% percents, ^ line continuations.
        var curl =
            "curl ^\"https://music.youtube.com/youtubei/v1/browse?prettyPrint=false^\" ^\r\n" +
            "  -H ^\"accept: */*^\" ^\r\n" +
            "  -b ^\"VISITOR_INFO1_LIVE=a^%^3D; SAPISID=s1/x; PREF=tz=Europe.Lisbon^\" ^\r\n" +
            "  --data-raw ^\"^{^\\^\"context^\\^\":^{^}^}^\"";

        Assert.Equal(
            "VISITOR_INFO1_LIVE=a%3D; SAPISID=s1/x; PREF=tz=Europe.Lisbon",
            CookieText.ExtractCookieHeader(curl));
    }

    [Fact]
    public void CurlWithoutCookies_FallsBackToTheText_WhichCantSignIn()
    {
        const string curl = "curl 'https://music.youtube.com/' -H 'accept: */*'";

        Assert.Equal(curl, CookieText.ExtractCookieHeader(curl));
        Assert.Null(InnerTubeSession.FromCookieString(curl));
    }

    [Fact]
    public void FromCookieString_AcceptsACurlCommand()
    {
        var session = InnerTubeSession.FromCookieString(
            "curl 'https://music.youtube.com/' -b '" + Cookies + "'");

        Assert.NotNull(session);
        Assert.Equal("s3/y", session!.Sapisid);
        Assert.Equal(Cookies, session.CookieHeader);
    }
}
