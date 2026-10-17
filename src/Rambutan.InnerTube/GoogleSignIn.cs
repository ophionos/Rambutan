namespace Rambutan.InnerTube;

/// <summary>
/// The Google sign-in behind a YouTube Music session: the cookies a request to
/// accounts.google.com carries. When the youtube.com cookies have expired,
/// Google issues new ones from it, as it does for a browser that opens YouTube
/// after a while away (<see cref="InnerTubeClient.ReissueSessionAsync"/>).
/// They give access to the whole Google account, so they are sent only to
/// accounts.google.com. Immutable, like <see cref="InnerTubeSession"/>.
/// </summary>
public sealed class GoogleSignIn
{
    public const string Host = "accounts.google.com";

    private GoogleSignIn(string cookieHeader) => CookieHeader = cookieHeader;

    public string CookieHeader { get; }

    /// <summary>
    /// Accepts what <see cref="InnerTubeSession.FromCookieString"/> accepts. Null
    /// unless the cookies hold a Google sign-in: SID, and one of the cookies only
    /// accounts.google.com receives (LSID, __Host-1PLSID, __Host-3PLSID). A
    /// youtube.com request never carries those, which also tells the two copies
    /// apart: both have SID and SAPISID.
    /// </summary>
    public static GoogleSignIn? FromCookieString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var pairs = CookiePairs.Parse(CookieText.ExtractCookieHeader(raw));
        return IsSignIn(pairs) ? new GoogleSignIn(CookiePairs.Join(pairs)) : null;
    }

    /// <summary>This sign-in with the cookies a response from
    /// <paramref name="responseHost"/> set applied, the way
    /// <see cref="InnerTubeSession.WithSetCookies"/> does for youtube.com. Returns
    /// this instance when nothing changed.</summary>
    public GoogleSignIn WithSetCookies(IEnumerable<string> setCookieHeaders, string responseHost, DateTimeOffset now)
    {
        var pairs = CookiePairs.Parse(CookieHeader);
        return CookiePairs.Merge(pairs, setCookieHeaders, Host, responseHost, now)
            ? new GoogleSignIn(CookiePairs.Join(pairs))
            : this;
    }

    private static bool IsSignIn(List<(string Name, string Value)> pairs) =>
        pairs.Exists(p => p.Name == "SID") &&
        pairs.Exists(p => p.Name is "LSID" or "__Host-1PLSID" or "__Host-3PLSID");
}
