using System.Text.Json.Serialization;

namespace Rambutan.Integrations.LastFm;

public sealed record LastFmSession(string Username, string Key);

/// <summary>
/// One play as Last.fm receives it. Also the on-disk shape of the offline queue,
/// so its property names are part of <c>scrobble-queue.json</c> version 1.
/// </summary>
public sealed record ScrobbleEntry
{
    public required string Artist { get; init; }
    public required string Track { get; init; }
    public string? Album { get; init; }

    /// <summary>Track length in whole seconds, when known.</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>When the play started, UTC unix seconds.</summary>
    public long Timestamp { get; init; }
}

/// <summary>What a call's result means for the caller.</summary>
public enum LastFmOutcome
{
    Ok,

    /// <summary>Error 9: the session key was revoked. The user must connect again.</summary>
    InvalidSession,

    /// <summary>Errors 8, 11, 16, network failures and unreadable responses: try again later.</summary>
    RetryLater,

    /// <summary>Error 29: too many calls. Back off longer than for <see cref="RetryLater"/>.</summary>
    RateLimited,

    /// <summary>Error 14 from auth.getSession: the user hasn't approved the token yet.</summary>
    NotApproved,

    /// <summary>Errors 4 and 15 from auth.getSession: the token is invalid or expired.</summary>
    TokenExpired,

    /// <summary>
    /// Errors 10, 13 and 26: this build's API key or signing is wrong, not the
    /// data. Nothing is dropped, and nothing will succeed until a fixed build runs.
    /// </summary>
    ClientMisconfigured,

    /// <summary>Any other error: this request's data is refused and retrying won't help.</summary>
    Rejected,
}

public readonly record struct LastFmResult<T>(LastFmOutcome Outcome, T? Value, int? ErrorCode = null, string? Message = null)
{
    public bool IsOk => Outcome == LastFmOutcome.Ok;

    public override string ToString() =>
        IsOk ? "ok" : $"{Outcome} (error {ErrorCode?.ToString() ?? "none"}: {Message})";
}

/// <summary>track.scrobble's verdict on a batch: how many were accepted and how many ignored.</summary>
public readonly record struct ScrobbleBatchResult(int Accepted, int Ignored);

internal sealed class LastFmErrorDto
{
    [JsonPropertyName("error")] public int? Error { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

internal sealed class TokenDto
{
    [JsonPropertyName("token")] public string? Token { get; set; }
}

internal sealed class SessionEnvelopeDto
{
    [JsonPropertyName("session")] public SessionDto? Session { get; set; }
}

internal sealed class SessionDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("key")] public string? Key { get; set; }
}

internal sealed class ScrobblesEnvelopeDto
{
    [JsonPropertyName("scrobbles")] public ScrobblesDto? Scrobbles { get; set; }
}

internal sealed class ScrobblesDto
{
    [JsonPropertyName("@attr")] public ScrobblesAttrDto? Attr { get; set; }
}

internal sealed class ScrobblesAttrDto
{
    [JsonPropertyName("accepted")] public int Accepted { get; set; }
    [JsonPropertyName("ignored")] public int Ignored { get; set; }
}

// Last.fm's JSON is a translation of its XML and sometimes quotes numbers.
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(LastFmErrorDto))]
[JsonSerializable(typeof(TokenDto))]
[JsonSerializable(typeof(SessionEnvelopeDto))]
[JsonSerializable(typeof(ScrobblesEnvelopeDto))]
internal sealed partial class LastFmJsonContext : JsonSerializerContext;
