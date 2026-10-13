using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Rambutan.App.Services.Demo;

/// <summary>
/// Serves generated artwork in place of the recorded thumbnail URLs, whose hosts a
/// demo can't reach: a diagonal two-colour gradient, stable per URL, so each album
/// is its own colour and the ambient background still has something to tint with.
/// Written as a 24-bit PNG, as real covers are JPEG or PNG: a download's kept
/// cover then goes through the same paths, export's embedding included.
/// The colour ignores a Google size suffix (<c>=w120-h120</c>, <c>=s544</c>), so the
/// same cover at two sizes looks the same, as it does live.
/// </summary>
/// <param name="offline">Every request fails, as with no network
/// (<c>RAMBUTAN_DEMO_FAIL=artwork</c>): only a download's kept cover still shows.</param>
public sealed partial class DemoArtworkHandler(bool offline = false) : HttpMessageHandler
{
    private const int Size = 256;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (offline)
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("demo: artwork offline"));
        var url = SizeSuffix().Replace(request.RequestUri?.ToString() ?? "", "");
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(url));
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Gradient(hash)),
        };
        return Task.FromResult(response);
    }

    private static byte[] Gradient(byte[] hash)
    {
        var (r1, g1, b1) = Hsv(hash[0] / 255.0 * 360, 0.65, 0.85);
        var (r2, g2, b2) = Hsv((hash[0] / 255.0 * 360 + 40 + hash[1] % 100) % 360, 0.75, 0.35);

        // One filter byte (0: none) before each row of RGB.
        var pixels = new byte[Size * (1 + Size * 3)];
        var at = 0;
        for (var y = 0; y < Size; y++)
        {
            pixels[at++] = 0;
            for (var x = 0; x < Size; x++)
            {
                var t = (x + y) / (2.0 * (Size - 1));
                pixels[at++] = (byte)(r1 + (r2 - r1) * t);
                pixels[at++] = (byte)(g1 + (g2 - g1) * t);
                pixels[at++] = (byte)(b1 + (b2 - b1) * t);
            }
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Size);
        header[8] = 8;  // bits per channel
        header[9] = 2;  // RGB

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(pixels);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR"u8, header);
        Chunk(png, "IDAT"u8, compressed.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        png.Write(word);
        png.Write(type);
        png.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc32(type, data));
        png.Write(word);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>PNG's CRC-32, over a chunk's type and data.</summary>
    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static (double R, double G, double B) Hsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (h / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((r + m) * 255, (g + m) * 255, (b + m) * 255);
    }

    [GeneratedRegex(@"=(w\d+-h\d+|s\d+)[^=/]*$")]
    private static partial Regex SizeSuffix();
}
