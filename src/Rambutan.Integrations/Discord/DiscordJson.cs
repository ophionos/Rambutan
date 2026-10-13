using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rambutan.Integrations.Discord;

/// <summary>
/// The Rich Presence activity Rambutan sends. A record so the service can tell
/// "nothing changed" by value and skip the send (Discord rate-limits
/// SET_ACTIVITY to five per twenty seconds).
/// </summary>
public sealed record DiscordActivity
{
    /// <summary>2 = "Listening to …".</summary>
    public int Type { get; init; } = 2;

    public string? Details { get; init; }
    public string? State { get; init; }
    public DiscordTimestamps? Timestamps { get; init; }
    public DiscordAssets? Assets { get; init; }
}

/// <summary>Unix milliseconds; Discord draws elapsed/remaining from these.</summary>
public sealed record DiscordTimestamps(long? Start, long? End);

/// <summary>An asset key uploaded to the Discord application, or an https URL.</summary>
public sealed record DiscordAssets(string? LargeImage, string? LargeText);

internal sealed record DiscordHandshake(int V, string ClientId);

internal sealed record DiscordSetActivityArgs(int Pid, DiscordActivity? Activity);

internal sealed record DiscordCommand(string Cmd, DiscordSetActivityArgs Args, string Nonce);

/// <summary>Anything Discord sends back: dispatches, command replies, close reasons.</summary>
internal sealed class DiscordIncoming
{
    public string? Cmd { get; set; }
    public string? Evt { get; set; }
    public JsonElement? Data { get; set; }
    public string? Nonce { get; set; }

    // Close frames (opcode 2) carry these at the top level.
    public int? Code { get; set; }
    public string? Message { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DiscordHandshake))]
[JsonSerializable(typeof(DiscordCommand))]
[JsonSerializable(typeof(DiscordIncoming))]
internal sealed partial class DiscordJsonContext : JsonSerializerContext;
