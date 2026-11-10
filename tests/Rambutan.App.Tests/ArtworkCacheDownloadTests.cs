using System.Net;
using Rambutan.App.Services;

namespace Rambutan.App.Tests;

/// <summary>What the artwork cache accepts from a host: any host answers, so the size is bounded.</summary>
public sealed class ArtworkCacheDownloadTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rambutan-artwork-");

    public void Dispose() => _dir.Delete(recursive: true);

    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond());
    }

    /// <summary>A body that never ends and counts what was read from it.</summary>
    private sealed class EndlessStream : Stream
    {
        public long Served;

        public override int Read(Span<byte> buffer)
        {
            buffer.Clear();
            Served += buffer.Length;
            return buffer.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Endless(EndlessStream body, long? declaredLength)
    {
        var content = new StreamContent(body);
        content.Headers.ContentLength = declaredLength;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private bool CachedAnything() =>
        Directory.Exists(Path.Combine(_dir.FullName, "cache", "artwork")) &&
        Directory.EnumerateFiles(Path.Combine(_dir.FullName, "cache", "artwork")).Any();

    [Fact]
    public async Task A_cover_of_a_normal_size_is_downloaded_and_cached()
    {
        byte[] cover = [1, 2, 3, 4];
        using var cache = new ArtworkCache(_dir.FullName, new Handler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(cover),
        }));

        Assert.Equal(cover, await cache.GetBytesAsync("https://example.test/a"));
        Assert.True(CachedAnything());
    }

    [Fact]
    public async Task A_download_with_no_declared_length_is_cut_off_at_the_limit()
    {
        var body = new EndlessStream();
        using var cache = new ArtworkCache(_dir.FullName, new Handler(() => Endless(body, null)));

        await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetBytesAsync("https://example.test/a"));

        Assert.InRange(body.Served, 1, ArtworkCache.MaxImageBytes + 81920);
        Assert.False(CachedAnything());
    }

    [Fact]
    public async Task A_download_declared_over_the_limit_is_refused_without_reading()
    {
        var body = new EndlessStream();
        using var cache = new ArtworkCache(_dir.FullName, new Handler(() => Endless(body, ArtworkCache.MaxImageBytes + 1)));

        await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetBytesAsync("https://example.test/a"));

        Assert.Equal(0, body.Served);
        Assert.False(CachedAnything());
    }

    [Fact]
    public async Task A_failed_status_is_still_a_failed_download()
    {
        using var cache = new ArtworkCache(_dir.FullName, new Handler(() => new HttpResponseMessage(HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetBytesAsync("https://example.test/a"));
    }
}
