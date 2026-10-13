using System.Security.Cryptography;
using System.Text;

namespace Rambutan.InnerTube;

/// <summary>
/// An authenticated YouTube Music session built from browser cookies
/// (the ytmusicapi browser-auth model). Immutable; the client
/// computes a fresh SAPISIDHASH per request. Google rotates some of the cookies,
/// so a refreshed copy is a new instance of the same sign-in
/// (<see cref="WithSetCookies"/>, <see cref="IsSameSignIn"/>).
/// </summary>
public sealed class InnerTubeSession
{
    private const string Origin = "https://music.youtube.com";

    // One per sign-in, shared by every refreshed copy of it.
    private readonly object _signIn;

    private InnerTubeSession(string cookieHeader, string sapisid, object signIn)
    {
        CookieHeader = cookieHeader;
        Sapisid = sapisid;
        _signIn = signIn;
    }

    public string CookieHeader { get; }
    public string Sapisid { get; }

    /// <summary>
    /// Accepts what the sign-in dialog was given: the Cookie header value, a
    /// "Cookie: …" line, a copied header block, or a copied cURL command (see
    /// <see cref="CookieText"/>). Returns null when the cookies can't
    /// authenticate (no SAPISID).
    /// </summary>
    public static InnerTubeSession? FromCookieString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var pairs = Parse(CookieText.ExtractCookieHeader(raw));
        return Create(pairs, new object());
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are the same
    /// sign-in, perhaps with different rotated cookies. Two nulls are the same
    /// (signed out); a pasted copy of the same cookies is a new sign-in.</summary>
    public static bool IsSameSignIn(InnerTubeSession? a, InnerTubeSession? b) =>
        a is null || b is null ? a is null && b is null : ReferenceEquals(a._signIn, b._signIn);

    /// <summary>
    /// This session with the cookies a response set applied, as a browser would
    /// keep them: a new value replaces the old one, a new cookie is added, and an
    /// expired one (Max-Age=0 or a past Expires) is removed. Only cookies that a
    /// music.youtube.com request would carry apply: Domain=youtube.com, or no
    /// Domain when the response came from music.youtube.com itself. Returns this
    /// instance when nothing changed, and null when the cookies left can't
    /// authenticate (no SAPISID).
    /// </summary>
    public InnerTubeSession? WithSetCookies(IEnumerable<string> setCookieHeaders, string responseHost, DateTimeOffset now)
    {
        var pairs = Parse(CookieHeader);
        var changed = false;
        foreach (var header in setCookieHeaders)
        {
            if (SetCookie.Parse(header, responseHost, now) is not { } cookie)
                continue;
            var index = pairs.FindIndex(p => p.Name == cookie.Name);
            if (cookie.Expired)
            {
                if (index >= 0)
                {
                    pairs.RemoveAt(index);
                    changed = true;
                }
            }
            else if (index < 0)
            {
                pairs.Add((cookie.Name, cookie.Value));
                changed = true;
            }
            else if (pairs[index].Value != cookie.Value)
            {
                pairs[index] = (cookie.Name, cookie.Value);
                changed = true;
            }
        }

        return changed ? Create(pairs, _signIn) : this;
    }

    private static List<(string Name, string Value)> Parse(string cookieHeader)
    {
        var pairs = new List<(string Name, string Value)>();
        foreach (var part in cookieHeader.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
                pairs.Add((part[..eq], part[(eq + 1)..]));
        }
        return pairs;
    }

    private static InnerTubeSession? Create(List<(string Name, string Value)> pairs, object signIn)
    {
        string? sapisid = null;
        foreach (var (name, value) in pairs)
        {
            if (name is "__Secure-3PAPISID" || (name is "SAPISID" && sapisid is null))
                sapisid = value;
        }

        return sapisid is null
            ? null
            : new InnerTubeSession(string.Join("; ", pairs.Select(p => $"{p.Name}={p.Value}")), sapisid, signIn);
    }

    /// <summary>Authorization header value: SAPISIDHASH {ts}_{SHA1("{ts} {SAPISID} {origin}")}.</summary>
    public string ComputeAuthorization(DateTimeOffset now)
    {
        var timestamp = now.ToUnixTimeSeconds();
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{timestamp} {Sapisid} {Origin}"));
        return $"SAPISIDHASH {timestamp}_{Convert.ToHexStringLower(hash)}";
    }
}

/// <summary>One Set-Cookie header, reduced to what a stored session needs.</summary>
internal sealed record SetCookie(string Name, string Value, bool Expired)
{
    /// <summary>Null for a malformed header, or one a music.youtube.com request
    /// wouldn't send back (another domain, or host-only on another host).</summary>
    public static SetCookie? Parse(string header, string responseHost, DateTimeOffset now)
    {
        var parts = header.Split(';', StringSplitOptions.TrimEntries);
        var eq = parts[0].IndexOf('=');
        if (eq <= 0)
            return null;

        string? domain = null;
        var expired = false;
        foreach (var attribute in parts.Skip(1))
        {
            var sep = attribute.IndexOf('=');
            var key = sep < 0 ? attribute : attribute[..sep];
            var value = sep < 0 ? "" : attribute[(sep + 1)..];
            if (key.Equals("Domain", StringComparison.OrdinalIgnoreCase))
                domain = value.TrimStart('.');
            else if (key.Equals("Max-Age", StringComparison.OrdinalIgnoreCase) &&
                     long.TryParse(value, out var maxAge) && maxAge <= 0)
                expired = true;
            else if (key.Equals("Expires", StringComparison.OrdinalIgnoreCase) &&
                     DateTimeOffset.TryParse(value.Replace('-', ' '), System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.AssumeUniversal, out var expires) &&
                     expires <= now)
                expired = true;
        }

        var applies = domain is null
            ? responseHost.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase)
            : domain.Equals("youtube.com", StringComparison.OrdinalIgnoreCase);
        return applies ? new SetCookie(parts[0][..eq].Trim(), parts[0][(eq + 1)..].Trim(), expired) : null;
    }
}

public enum SessionState
{
    Anonymous,
    Valid,
    Expired,

    /// <summary>The probe couldn't reach YouTube Music — says nothing about the
    /// cookies; never treat as expired.</summary>
    Unreachable,
}

public sealed record SessionInfo(SessionState State, string? AccountName, string? AccountPhotoUrl = null);
