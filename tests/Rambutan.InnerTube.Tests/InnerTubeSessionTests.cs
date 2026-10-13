namespace Rambutan.InnerTube.Tests;

public class InnerTubeSessionTests
{
    [Fact]
    public void FromCookieString_ParsesHeaderLineOrBareValue()
    {
        const string bare = "VISITOR_INFO1_LIVE=abc; __Secure-3PAPISID=secret123; PREF=f1";

        var fromBare = InnerTubeSession.FromCookieString(bare);
        var fromLine = InnerTubeSession.FromCookieString("Cookie: " + bare);

        Assert.NotNull(fromBare);
        Assert.Equal("secret123", fromBare!.Sapisid);
        Assert.Equal(fromBare.CookieHeader, fromLine!.CookieHeader);
        Assert.Contains("__Secure-3PAPISID=secret123", fromBare.CookieHeader);
    }

    [Fact]
    public void FromCookieString_PrefersSecure3PapisidOverSapisid()
    {
        var session = InnerTubeSession.FromCookieString(
            "SAPISID=fallback; __Secure-3PAPISID=preferred");

        Assert.Equal("preferred", session!.Sapisid);
    }

    [Fact]
    public void FromCookieString_WithoutSapisid_ReturnsNull()
    {
        Assert.Null(InnerTubeSession.FromCookieString("VISITOR_INFO1_LIVE=abc; PREF=f1"));
        Assert.Null(InnerTubeSession.FromCookieString("   "));
    }

    [Fact]
    public void ComputeAuthorization_MatchesKnownVector()
    {
        var session = InnerTubeSession.FromCookieString("__Secure-3PAPISID=test");
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1700000000);

        var auth = session!.ComputeAuthorization(timestamp);

        // SHA1("1700000000 test https://music.youtube.com")
        Assert.Equal("SAPISIDHASH 1700000000_2fcda7881326d555d9cebfca8988a307518c7743", auth);
    }
}
