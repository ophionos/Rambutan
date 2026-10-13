using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Integrations.Discord;

namespace Rambutan.Integrations.Tests;

/// <summary>
/// The service against an in-memory Discord: no socket, no real client. Proves
/// the handshake, what gets sent when, ping replies, refusal and the goodbye.
/// </summary>
public class DiscordPresenceServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static TrackInfo Track(string title = "Get Lucky") => new()
    {
        VideoId = "v1",
        Title = title,
        Artists = [new ArtistRef("Daft Punk")],
        Duration = TimeSpan.FromSeconds(248),
        ThumbnailUrl = "https://lh3.googleusercontent.com/abc",
    };

    [Fact]
    public async Task Handshakes_ThenShowsWhatIsPlaying()
    {
        var discord = new FakeDiscord();
        using var service = new DiscordPresenceService("1234", discord.ConnectAsync);
        service.OnNowPlayingChanged(Track());
        service.OnStateChanged(PlaybackState.Playing);

        service.Configure(true, new DiscordPresenceOptions());

        var hello = await discord.NextAsync();
        Assert.Equal(DiscordOpcode.Handshake, hello.Opcode);
        Assert.Equal("1234", hello.Json.GetProperty("client_id").GetString());
        await discord.SendAsync(DiscordOpcode.Frame, "{\"cmd\":\"DISPATCH\",\"evt\":\"READY\",\"data\":{\"v\":1}}");

        var set = await discord.NextAsync();
        Assert.Equal("SET_ACTIVITY", set.Json.GetProperty("cmd").GetString());
        var activity = set.Json.GetProperty("args").GetProperty("activity");
        Assert.Equal(2, activity.GetProperty("type").GetInt32());
        Assert.Equal("Get Lucky", activity.GetProperty("details").GetString());
        Assert.Equal("Daft Punk", activity.GetProperty("state").GetString());
        Assert.True(activity.GetProperty("timestamps").TryGetProperty("end", out _));
        Assert.Equal(Environment.ProcessId, set.Json.GetProperty("args").GetProperty("pid").GetInt32());
        await WaitForAsync(() => service.ConnectionState == DiscordConnectionState.Connected);
    }

    [Fact]
    public async Task ANewTrack_IsSent_ButUnchangedTicksAreNot()
    {
        var (discord, service) = await ConnectedAsync();
        using (service)
        {
            service.OnNowPlayingChanged(Track());
            service.OnStateChanged(PlaybackState.Playing);
            Assert.Equal("Get Lucky", (await discord.NextActivityAsync())?.GetProperty("details").GetString());

            service.OnProgress(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(248));
            service.OnNowPlayingChanged(Track("Instant Crush"));

            Assert.Equal("Instant Crush", (await discord.NextActivityAsync())?.GetProperty("details").GetString());
        }
    }

    [Fact]
    public async Task Stopping_ClearsThePresence()
    {
        var (discord, service) = await ConnectedAsync();
        using (service)
        {
            service.OnNowPlayingChanged(Track());
            service.OnStateChanged(PlaybackState.Playing);
            Assert.NotNull(await discord.NextActivityAsync());

            service.OnStateChanged(PlaybackState.Stopped);

            Assert.Null(await discord.NextActivityAsync());
        }
    }

    [Fact]
    public async Task TurningItOff_ClearsSaysGoodbyeAndReportsOff()
    {
        var (discord, service) = await ConnectedAsync();
        using (service)
        {
            service.OnNowPlayingChanged(Track());
            service.OnStateChanged(PlaybackState.Playing);
            Assert.NotNull(await discord.NextActivityAsync());

            service.Configure(false, new DiscordPresenceOptions());

            Assert.Null(await discord.NextActivityAsync());
            Assert.Equal(DiscordOpcode.Close, (await discord.NextAsync()).Opcode);
            await WaitForAsync(() => service.ConnectionState == DiscordConnectionState.Off);
        }
    }

    [Fact]
    public async Task TurningItOff_OverARealSocket_StillSaysGoodbye()
    {
        // Seen in demo mode against a fake Discord socket: turning presence off
        // closed the socket without the clear or the close frame, because the
        // reader stopped by the same cancellation could win the race and read as
        // a lost connection. Timing-dependent, so this guards the path rather
        // than reproducing the race deterministically.
        var path = Path.Combine(Path.GetTempPath(), $"rbt-{Guid.NewGuid():N}"[..16]);
        using var listener = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        try
        {
            using var service = new DiscordPresenceService("1234", async ct =>
            {
                var client = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                await client.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(path), ct);
                return new System.Net.Sockets.NetworkStream(client, ownsSocket: true);
            });
            service.OnNowPlayingChanged(Track());
            service.OnStateChanged(PlaybackState.Playing);
            service.Configure(true, new DiscordPresenceOptions());

            using var accepted = await listener.AcceptAsync().WaitAsync(Patience);
            await using var discord = new System.Net.Sockets.NetworkStream(accepted);
            Assert.Equal(DiscordOpcode.Handshake, (await ReadAsync(discord)).Opcode);
            await discord.WriteAsync(DiscordIpcFrame.Encode(DiscordOpcode.Frame, "{\"evt\":\"READY\"}"));
            Assert.Equal(DiscordOpcode.Frame, (await ReadAsync(discord)).Opcode);

            service.Configure(false, new DiscordPresenceOptions());

            var clear = await ReadAsync(discord);
            Assert.False(clear.Json.GetProperty("args").TryGetProperty("activity", out _));
            Assert.Equal(DiscordOpcode.Close, (await ReadAsync(discord)).Opcode);
        }
        finally
        {
            File.Delete(path);
        }

        static async Task<(DiscordOpcode Opcode, JsonElement Json)> ReadAsync(Stream stream)
        {
            using var timeout = new CancellationTokenSource(Patience);
            var frame = await DiscordIpcFrame.ReadAsync(stream, timeout.Token) ?? throw new EndOfStreamException();
            return (frame.Opcode, JsonDocument.Parse(frame.Payload).RootElement.Clone());
        }
    }

    [Fact]
    public async Task APing_IsAnsweredWithTheSamePayload()
    {
        var (discord, service) = await ConnectedAsync();
        using (service)
        {
            await discord.SendAsync(DiscordOpcode.Ping, "{\"n\":7}");

            var pong = await discord.NextAsync();
            Assert.Equal(DiscordOpcode.Pong, pong.Opcode);
            Assert.Equal(7, pong.Json.GetProperty("n").GetInt32());
        }
    }

    [Fact]
    public async Task NoDiscord_MeansWaiting_WithNoRefusal()
    {
        using var service = new DiscordPresenceService("1234", _ => Task.FromResult<Stream?>(null));

        service.Configure(true, new DiscordPresenceOptions());

        await WaitForAsync(() => service.ConnectionState == DiscordConnectionState.Waiting);
        Assert.False(service.IsRefused);
        Assert.Null(service.Refusal);
    }

    [Fact]
    public async Task ARefusedHandshake_IsWaitingWithTheReason()
    {
        var discord = new FakeDiscord();
        using var service = new DiscordPresenceService("1234", discord.ConnectAsync);
        service.Configure(true, new DiscordPresenceOptions());

        Assert.Equal(DiscordOpcode.Handshake, (await discord.NextAsync()).Opcode);
        await discord.SendAsync(DiscordOpcode.Close, "{\"code\":4000,\"message\":\"Invalid Client ID\"}");

        await WaitForAsync(() => service.Refusal == "Invalid Client ID");
        Assert.True(service.IsRefused);
        Assert.Equal(DiscordConnectionState.Waiting, service.ConnectionState);
    }

    [Fact]
    public async Task ARefusalWithoutAReason_IsStillARefusal()
    {
        var discord = new FakeDiscord();
        using var service = new DiscordPresenceService("1234", discord.ConnectAsync);
        service.Configure(true, new DiscordPresenceOptions());

        Assert.Equal(DiscordOpcode.Handshake, (await discord.NextAsync()).Opcode);
        await discord.SendAsync(DiscordOpcode.Close, "{\"code\":4000}");

        await WaitForAsync(() => service.IsRefused);
        Assert.Null(service.Refusal);
    }

    [Fact]
    public void WithoutAnAppId_ItNeverStarts()
    {
        var connects = 0;
        using var service = new DiscordPresenceService(null, _ =>
        {
            connects++;
            return Task.FromResult<Stream?>(null);
        });

        service.Configure(true, new DiscordPresenceOptions());
        service.OnNowPlayingChanged(Track());

        Assert.False(service.IsAvailable);
        Assert.Equal(DiscordConnectionState.Off, service.ConnectionState);
        Assert.Equal(0, connects);
    }

    [Fact]
    public void ListenerCalls_DontWaitForDiscord()
    {
        // A connect that never finishes must not hold up playback events.
        using var service = new DiscordPresenceService("1234", ct => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => (Stream?)null));
        service.Configure(true, new DiscordPresenceOptions());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
            service.OnProgress(TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(2000));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    private static async Task<(FakeDiscord, DiscordPresenceService)> ConnectedAsync()
    {
        var discord = new FakeDiscord();
        var service = new DiscordPresenceService("1234", discord.ConnectAsync);
        service.Configure(true, new DiscordPresenceOptions());
        Assert.Equal(DiscordOpcode.Handshake, (await discord.NextAsync()).Opcode);
        await discord.SendAsync(DiscordOpcode.Frame, "{\"cmd\":\"DISPATCH\",\"evt\":\"READY\"}");
        await WaitForAsync(() => service.ConnectionState == DiscordConnectionState.Connected);
        return (discord, service);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the service");
            await Task.Delay(10);
        }
    }

    /// <summary>The Discord end of an in-memory duplex stream.</summary>
    private sealed class FakeDiscord
    {
        private readonly Pipe _toDiscord = new();
        private readonly Pipe _toApp = new();

        public Task<Stream?> ConnectAsync(CancellationToken ct) =>
            Task.FromResult<Stream?>(new Duplex(_toApp.Reader.AsStream(), _toDiscord.Writer.AsStream()));

        public async Task<(DiscordOpcode Opcode, JsonElement Json)> NextAsync()
        {
            using var timeout = new CancellationTokenSource(Patience);
            var frame = await DiscordIpcFrame.ReadAsync(_toDiscord.Reader.AsStream(leaveOpen: true), timeout.Token)
                        ?? throw new EndOfStreamException();
            return (frame.Opcode, JsonDocument.Parse(frame.Payload).RootElement.Clone());
        }

        /// <summary>The next SET_ACTIVITY's activity; null for a clear.</summary>
        public async Task<JsonElement?> NextActivityAsync()
        {
            var (opcode, json) = await NextAsync();
            Assert.Equal(DiscordOpcode.Frame, opcode);
            Assert.Equal("SET_ACTIVITY", json.GetProperty("cmd").GetString());
            return json.GetProperty("args").TryGetProperty("activity", out var a) ? a : null;
        }

        public async Task SendAsync(DiscordOpcode opcode, string json)
        {
            await _toApp.Writer.WriteAsync(DiscordIpcFrame.Encode(opcode, Encoding.UTF8.GetBytes(json)));
        }
    }

    private sealed class Duplex(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => write.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                read.Dispose();
                write.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
