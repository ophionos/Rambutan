using System.Security.Cryptography;
using System.Text;

namespace Rambutan.Core.Remote;

/// <summary>
/// The secret a phone proves it was paired with. 256 random bits, so guessing
/// it over the network is hopeless and no rate limit is needed; it travels in
/// the pairing link's fragment and then in an Authorization header.
/// </summary>
public static class PairingToken
{
    public static string Create()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool Matches(string? expected, string? presented)
    {
        if (string.IsNullOrEmpty(expected) || presented is null)
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
    }

    /// <summary>Whether a stored value looks like a token this class made, so a
    /// damaged file is replaced instead of trusted.</summary>
    public static bool IsWellFormed(string? token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
