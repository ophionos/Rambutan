using System.Security.Cryptography;
using System.Text;

namespace Rambutan.Integrations.LastFm;

/// <summary>
/// Last.fm API method signatures (last.fm/api/authspec §8): every parameter
/// except <c>format</c> and <c>callback</c>, sorted by name, concatenated as
/// name then value with no separators, the shared secret appended, and the MD5
/// of the UTF-8 bytes as lowercase hex.
/// </summary>
public static class LastFmSignature
{
    public static string Sign(IEnumerable<KeyValuePair<string, string>> parameters, string sharedSecret)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in parameters
                     .Where(p => p.Key is not ("format" or "callback"))
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append(name).Append(value);
        }

        text.Append(sharedSecret);
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
