using System.Security.Cryptography;

namespace Rambutan.Streams;

/// <summary>A sidecar's download whose SHA-256 isn't the one expected.</summary>
public sealed class SidecarIntegrityException(string what, string expected, string actual)
    : InvalidOperationException(
        $"{what} was not installed: its SHA-256 is {actual}, expected {expected}. " +
        "The download was damaged or is not the published file.");

/// <summary>
/// Downloads a sidecar to a file and checks it before anything runs or loads
/// it: TLS says which host answered, not that the file is the one published.
/// </summary>
internal static class VerifiedDownload
{
    /// <summary>Downloads <paramref name="url"/> to <paramref name="path"/> and
    /// returns its SHA-256 as lowercase hex, hashed while it is written.</summary>
    /// <exception cref="HttpRequestException">The download failed.</exception>
    public static async Task<string> ToFileAsync(HttpClient http, string url, string path, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = await http.GetStreamAsync(url, ct);
        await using (var target = File.Create(path))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Deletes <paramref name="path"/> and throws unless
    /// <paramref name="actual"/> is <paramref name="expected"/>.</summary>
    /// <exception cref="SidecarIntegrityException">The hashes differ.</exception>
    public static void Require(string what, string expected, string actual, string path)
    {
        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        throw new SidecarIntegrityException(what, expected, actual);
    }

    /// <summary>
    /// The hash a checksum file gives <paramref name="fileName"/>: lines of a
    /// SHA-256 in hex, then the name, as <c>sha256sum</c> writes them (the name
    /// after two spaces, or a space and <c>*</c>). Null when the file has no
    /// line for that exact name.
    /// </summary>
    public static string? HashFor(string checksums, string fileName)
    {
        foreach (var raw in checksums.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 66 || line[64] != ' ')
                continue;
            var hash = line.AsSpan(0, 64);
            if (hash.ContainsAnyExcept(HexDigits))
                continue;
            var name = line.AsSpan(65).TrimStart(' ').TrimStart('*');
            if (name.SequenceEqual(fileName))
                return hash.ToString();
        }
        return null;
    }

    private static readonly System.Buffers.SearchValues<char> HexDigits =
        System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");
}
