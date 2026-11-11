using System.Net;
using System.Text;
using Rambutan.Core.IO;

namespace Rambutan.Core.Tests;

public class BoundedReadTests
{
    /// <summary>A body that never ends, and counts what was read from it.</summary>
    private sealed class EndlessContent(long? declared) : HttpContent
    {
        public long Served;

        private sealed class Zeros(EndlessContent owner) : Stream
        {
            public override int Read(byte[] buffer, int offset, int count)
            {
                Array.Clear(buffer, offset, count);
                owner.Served += count;
                return count;
            }

            public override int Read(Span<byte> buffer)
            {
                buffer.Clear();
                owner.Served += buffer.Length;
                return buffer.Length;
            }

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

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Zeros(this));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = declared ?? 0;
            return declared is not null;
        }
    }

    [Fact]
    public void A_stream_at_the_limit_is_read_whole() =>
        Assert.Equal(100, BoundedRead.ReadAll(new MemoryStream(new byte[100]), 100).Length);

    [Fact]
    public void A_stream_over_the_limit_is_refused() =>
        Assert.Throws<InputTooLargeException>(() => BoundedRead.ReadAll(new MemoryStream(new byte[101]), 100));

    [Fact]
    public async Task An_async_stream_over_the_limit_is_refused() =>
        await Assert.ThrowsAsync<InputTooLargeException>(() =>
            BoundedRead.ReadAllAsync(new MemoryStream(new byte[300_000]), 200_000));

    [Fact]
    public async Task A_body_at_the_limit_is_read_in_its_declared_charset()
    {
        var content = new StringContent("Café", Encoding.Latin1, "text/plain");

        Assert.Equal("Café", await BoundedRead.ReadContentStringAsync(content, content.Headers.ContentLength!.Value));
    }

    [Fact]
    public async Task A_declared_length_over_the_limit_is_refused_before_reading()
    {
        var content = new EndlessContent(declared: 1_000_000);

        await Assert.ThrowsAsync<HttpRequestException>(() => BoundedRead.ReadContentAsync(content, 1000));

        Assert.Equal(0, content.Served);
    }

    [Fact]
    public async Task A_body_with_no_declared_length_is_cut_off_at_the_limit()
    {
        var content = new EndlessContent(declared: null);

        await Assert.ThrowsAsync<HttpRequestException>(() => BoundedRead.ReadContentAsync(content, 1000));

        Assert.InRange(content.Served, 1, 1000 + 81920);
    }
}
