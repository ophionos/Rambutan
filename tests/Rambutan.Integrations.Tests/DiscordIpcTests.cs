using System.Text;
using Rambutan.Integrations.Discord;

namespace Rambutan.Integrations.Tests;

public class DiscordIpcTests
{
    [Fact]
    public void Encode_WritesLittleEndianOpcodeAndLengthThenPayload()
    {
        var frame = DiscordIpcFrame.Encode(DiscordOpcode.Frame, "{}");

        Assert.Equal(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0, (byte)'{', (byte)'}' }, frame);
    }

    [Fact]
    public async Task Read_RoundTripsFramesBackToBack()
    {
        var stream = new MemoryStream();
        stream.Write(DiscordIpcFrame.Encode(DiscordOpcode.Handshake, "{\"v\":1}"));
        stream.Write(DiscordIpcFrame.Encode(DiscordOpcode.Ping, "ünï"));
        stream.Position = 0;

        var first = await DiscordIpcFrame.ReadAsync(stream, default);
        var second = await DiscordIpcFrame.ReadAsync(stream, default);
        var end = await DiscordIpcFrame.ReadAsync(stream, default);

        Assert.Equal(DiscordOpcode.Handshake, first!.Value.Opcode);
        Assert.Equal("{\"v\":1}", Encoding.UTF8.GetString(first.Value.Payload));
        Assert.Equal(DiscordOpcode.Ping, second!.Value.Opcode);
        Assert.Equal("ünï", Encoding.UTF8.GetString(second.Value.Payload));
        Assert.Null(end);
    }

    [Fact]
    public async Task Read_ReassemblesAFrameThatArrivesInPieces()
    {
        var bytes = DiscordIpcFrame.Encode(DiscordOpcode.Frame, "{\"evt\":\"READY\"}");
        var stream = new TrickleStream(bytes);

        var frame = await DiscordIpcFrame.ReadAsync(stream, default);

        Assert.Equal("{\"evt\":\"READY\"}", Encoding.UTF8.GetString(frame!.Value.Payload));
    }

    [Fact]
    public async Task Read_RejectsATruncatedFrame()
    {
        var bytes = DiscordIpcFrame.Encode(DiscordOpcode.Frame, "{\"a\":1}");
        var stream = new MemoryStream(bytes[..^2]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => DiscordIpcFrame.ReadAsync(stream, default));
    }

    [Theory]
    [InlineData(9, 2)]
    [InlineData(1, -1)]
    [InlineData(1, 1 << 21)]
    public async Task Read_RejectsAnOutOfStepHeader(int opcode, int length)
    {
        var header = new byte[8];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), opcode);
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), length);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => DiscordIpcFrame.ReadAsync(new MemoryStream(header), default));
    }

    [Fact]
    public void Handshake_IsVersionOneWithTheClientId() =>
        Assert.Equal("{\"v\":1,\"client_id\":\"1234\"}", DiscordIpcFrame.HandshakeJson("1234"));

    [Fact]
    public void SetActivity_UsesDiscordsFieldNamesAndLeavesOutWhatIsntShown()
    {
        var activity = new DiscordActivity
        {
            Details = "Title",
            State = "Artist",
            Timestamps = new DiscordTimestamps(1000, null),
            Assets = new DiscordAssets("https://example.com/a.jpg", null),
        };

        var json = DiscordIpcFrame.SetActivityJson(42, activity, "7");

        Assert.Equal(
            "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":42,\"activity\":{\"type\":2,\"details\":\"Title\",\"state\":\"Artist\"," +
            "\"timestamps\":{\"start\":1000},\"assets\":{\"large_image\":\"https://example.com/a.jpg\"}}},\"nonce\":\"7\"}",
            json);
    }

    [Fact]
    public void SetActivity_WithoutAnActivityClearsThePresence() =>
        Assert.Equal(
            "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":42},\"nonce\":\"1\"}",
            DiscordIpcFrame.SetActivityJson(42, null, "1"));

    [Fact]
    public void Incoming_ReadsDispatchesAndCloseReasons()
    {
        var ready = DiscordIpcFrame.ParseIncoming(Encoding.UTF8.GetBytes(
            "{\"cmd\":\"DISPATCH\",\"evt\":\"READY\",\"data\":{\"v\":1,\"user\":{\"id\":\"1\"}},\"nonce\":null}"));
        var close = DiscordIpcFrame.ParseIncoming(Encoding.UTF8.GetBytes(
            "{\"code\":4000,\"message\":\"Invalid Client ID\"}"));

        Assert.Equal("READY", ready!.Evt);
        Assert.Equal(4000, close!.Code);
        Assert.Equal("Invalid Client ID", close.Message);
        Assert.Null(DiscordIpcFrame.ParseIncoming(Encoding.UTF8.GetBytes("not json")));
    }

    [Fact]
    public void Paths_OnWindowsAreTheTenPipeSlots() =>
        Assert.Equal(
            Enumerable.Range(0, 10).Select(i => $"discord-ipc-{i}"),
            DiscordIpcPaths.Candidates(windows: true, _ => null));

    [Fact]
    public void Paths_OnLinuxTryTheRuntimeDirFirst_ThenTempDirs_IncludingFlatpakAndSnap()
    {
        var env = new Dictionary<string, string?>
        {
            ["XDG_RUNTIME_DIR"] = "/run/user/1000/",
            ["TMPDIR"] = "/tmp",
        };

        var paths = DiscordIpcPaths.Candidates(windows: false, k => env.GetValueOrDefault(k));

        Assert.Equal("/run/user/1000/discord-ipc-0", paths[0]);
        Assert.Contains("/run/user/1000/app/com.discordapp.Discord/discord-ipc-0", paths);
        Assert.Contains("/run/user/1000/snap.discord/discord-ipc-9", paths);
        Assert.Contains("/tmp/discord-ipc-3", paths);
        Assert.Equal(paths.Count, paths.Distinct().Count()); // /tmp is listed once
        Assert.Equal(2 * 3 * 10, paths.Count);
    }

    [Fact]
    public void Paths_FallBackToTmpWithNoEnvironment() =>
        Assert.Equal("/tmp/discord-ipc-0", DiscordIpcPaths.Candidates(windows: false, _ => null)[0]);

    /// <summary>Hands out one byte per read, like a socket under load.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct);
    }
}
