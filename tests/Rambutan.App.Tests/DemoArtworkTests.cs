using System.Buffers.Binary;
using System.Net.Http;
using Rambutan.App.Services.Demo;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>
/// Demo covers are real PNGs, so the artwork cache and the download
/// store name them by what they are, as they do a cover from YouTube Music.
/// </summary>
public class DemoArtworkTests
{
    private static async Task<byte[]> FetchAsync(string url)
    {
        using var client = new HttpClient(new DemoArtworkHandler());
        return await client.GetByteArrayAsync(url);
    }

    [Fact]
    public async Task ACover_IsA256SquarePng()
    {
        var bytes = await FetchAsync("https://lh3.googleusercontent.com/demo=w544-h544");

        Assert.Equal(".png", ArtworkUrls.ExtensionFor(bytes));
        Assert.Equal("IHDR"u8.ToArray(), bytes[12..16]);
        Assert.Equal(256u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)));
        Assert.Equal(256u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)));
        Assert.Equal("IEND"u8.ToArray(), bytes[^8..^4]);
    }

    [Fact]
    public async Task TheSameCover_AtAnotherSize_IsTheSameImage() =>
        Assert.Equal(
            await FetchAsync("https://lh3.googleusercontent.com/demo=w60-h60"),
            await FetchAsync("https://lh3.googleusercontent.com/demo=w544-h544"));
}
