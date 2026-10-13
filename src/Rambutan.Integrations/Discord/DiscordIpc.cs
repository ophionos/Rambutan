using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Rambutan.Integrations.Discord;

public enum DiscordOpcode
{
    Handshake = 0,
    Frame = 1,
    Close = 2,
    Ping = 3,
    Pong = 4,
}

/// <summary>
/// Discord's local RPC framing: a little-endian int32 opcode, an int32 payload
/// length, then that many bytes of UTF-8 JSON. Hand-rolled because the
/// DiscordRichPresence package failed NativeAOT vetting (Newtonsoft.Json).
/// </summary>
public static class DiscordIpcFrame
{
    public const int HeaderSize = 8;

    /// <summary>Larger than anything Discord sends (READY with a user object is a
    /// few KB); a bigger length means the stream is out of step.</summary>
    public const int MaxPayload = 1 << 20;

    public static byte[] Encode(DiscordOpcode opcode, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, (int)opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static byte[] Encode(DiscordOpcode opcode, string json) =>
        Encode(opcode, Encoding.UTF8.GetBytes(json));

    /// <summary>Reads one frame; null when the other side closed the stream cleanly
    /// between frames. Throws <see cref="InvalidDataException"/> on a malformed header.</summary>
    public static async Task<(DiscordOpcode Opcode, byte[] Payload)?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        if (!await ReadExactlyOrEofAsync(stream, header, ct))
            return null;

        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (opcode is < 0 or > (int)DiscordOpcode.Pong || length is < 0 or > MaxPayload)
            throw new InvalidDataException($"bad Discord frame header (opcode {opcode}, length {length})");

        var payload = new byte[length];
        if (length > 0 && !await ReadExactlyOrEofAsync(stream, payload, ct))
            throw new EndOfStreamException("Discord closed the connection mid-frame");
        return ((DiscordOpcode)opcode, payload);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                if (read == 0)
                    return false;
                throw new EndOfStreamException("Discord closed the connection mid-frame");
            }

            read += n;
        }

        return true;
    }

    internal static string HandshakeJson(string clientId) =>
        JsonSerializer.Serialize(new DiscordHandshake(1, clientId), DiscordJsonContext.Default.DiscordHandshake);

    internal static string SetActivityJson(int pid, DiscordActivity? activity, string nonce) =>
        JsonSerializer.Serialize(
            new DiscordCommand("SET_ACTIVITY", new DiscordSetActivityArgs(pid, activity), nonce),
            DiscordJsonContext.Default.DiscordCommand);

    internal static DiscordIncoming? ParseIncoming(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, DiscordJsonContext.Default.DiscordIncoming);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Where a running Discord client listens.</summary>
public static class DiscordIpcPaths
{
    public const int Slots = 10;

    // Relative to the runtime dir: native, Flatpak and Snap installs.
    private static readonly string[] UnixSubdirs = ["", "app/com.discordapp.Discord/", "snap.discord/"];

    /// <summary>Pipe names on Windows (for <see cref="NamedPipeClientStream"/>),
    /// socket paths elsewhere, in the order to try them.</summary>
    public static IReadOnlyList<string> Candidates(bool windows, Func<string, string?> env)
    {
        var result = new List<string>();
        if (windows)
        {
            for (var i = 0; i < Slots; i++)
                result.Add($"discord-ipc-{i}");
            return result;
        }

        var bases = new[] { env("XDG_RUNTIME_DIR"), env("TMPDIR"), env("TMP"), env("TEMP"), "/tmp" }
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b!.TrimEnd('/'))
            .Distinct(StringComparer.Ordinal);
        foreach (var dir in bases)
        {
            foreach (var sub in UnixSubdirs)
            {
                for (var i = 0; i < Slots; i++)
                    result.Add($"{dir}/{sub}discord-ipc-{i}");
            }
        }

        return result;
    }

    /// <summary>Connects to the first candidate that answers; null when Discord
    /// isn't running (or isn't reachable from this sandbox).</summary>
    public static async Task<Stream?> ConnectAsync(CancellationToken ct)
    {
        var windows = OperatingSystem.IsWindows();
        foreach (var candidate in Candidates(windows, Environment.GetEnvironmentVariable))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (windows)
                {
                    var pipe = new NamedPipeClientStream(".", candidate, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try
                    {
                        await pipe.ConnectAsync(200, ct);
                        return pipe;
                    }
                    catch
                    {
                        await pipe.DisposeAsync();
                        throw;
                    }
                }

                if (!File.Exists(candidate))
                    continue;
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(candidate), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Stale socket file or a pipe slot nobody serves: try the next one.
            }
        }

        return null;
    }
}
