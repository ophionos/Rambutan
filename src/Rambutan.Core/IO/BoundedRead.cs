using System.Text;

namespace Rambutan.Core.IO;

/// <summary>A stream held more bytes than the reader allows.</summary>
public sealed class InputTooLargeException(long limit)
    : IOException($"The input is larger than {limit} bytes.");

/// <summary>
/// Reads a stream or an HTTP body whole into memory, but never more than a
/// limit: a length that is declared can lie (a zip entry, a response with no
/// <c>Content-Length</c>), so the bytes actually read are counted too.
/// </summary>
public static class BoundedRead
{
    private const int ChunkBytes = 81920;

    /// <exception cref="InputTooLargeException">The stream has more than <paramref name="limit"/> bytes.</exception>
    public static byte[] ReadAll(Stream stream, long limit)
    {
        using var result = new MemoryStream();
        var chunk = new byte[ChunkBytes];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (result.Length + read > limit)
                throw new InputTooLargeException(limit);
            result.Write(chunk, 0, read);
        }
        return result.ToArray();
    }

    /// <exception cref="InputTooLargeException">The stream has more than <paramref name="limit"/> bytes.</exception>
    public static async Task<byte[]> ReadAllAsync(Stream stream, long limit, CancellationToken ct = default)
    {
        using var result = new MemoryStream();
        var chunk = new byte[ChunkBytes];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (result.Length + read > limit)
                throw new InputTooLargeException(limit);
            result.Write(chunk, 0, read);
        }
        return result.ToArray();
    }

    /// <summary>
    /// A response body, refused up front when its <c>Content-Length</c> is over
    /// the limit and cut off when the bytes read pass it. Send the request with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, or the client has
    /// buffered the body by now.
    /// </summary>
    /// <exception cref="HttpRequestException">The body is larger than <paramref name="limit"/>.</exception>
    public static async Task<byte[]> ReadContentAsync(HttpContent content, long limit, CancellationToken ct = default)
    {
        if (content.Headers.ContentLength > limit)
            throw TooLarge(limit, null);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(ct);
            return await ReadAllAsync(stream, limit, ct);
        }
        catch (InputTooLargeException ex)
        {
            throw TooLarge(limit, ex);
        }
    }

    /// <summary>The body as text, in the charset it declares (UTF-8 without one).</summary>
    /// <exception cref="HttpRequestException">The body is larger than <paramref name="limit"/>.</exception>
    public static async Task<string> ReadContentStringAsync(HttpContent content, long limit, CancellationToken ct = default)
    {
        var bytes = await ReadContentAsync(content, limit, ct);
        Encoding encoding = Encoding.UTF8;
        if (content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
            }
        }
        return encoding.GetString(bytes);
    }

    private static HttpRequestException TooLarge(long limit, Exception? inner) =>
        new($"The response is larger than {limit} bytes.", inner);
}
