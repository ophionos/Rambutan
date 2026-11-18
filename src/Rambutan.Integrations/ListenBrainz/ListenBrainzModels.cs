using System.Text.Json.Serialization;

namespace Rambutan.Integrations.ListenBrainz;

/// <summary>What a call's result means for the caller.</summary>
public enum ListenBrainzOutcome
{
    Ok,

    /// <summary>The token was refused (HTTP 401, or validate-token said it is not valid). The user must enter a token again.</summary>
    InvalidToken,

    /// <summary>The request's data is refused (400 and other 4xx) and retrying will not help.</summary>
    Rejected,

    /// <summary>HTTP 429: too many calls. Back off longer than for <see cref="RetryLater"/>.</summary>
    RateLimited,

    /// <summary>A 5xx, a network failure or a timeout: try again later.</summary>
    RetryLater,

    /// <summary>The server answered success with something that is not a ListenBrainz API answer.</summary>
    NotAServer,
}

/// <summary>
/// The outcome of one call. <see cref="Message"/> is for the log: it never
/// holds the token.
/// </summary>
public readonly record struct ListenBrainzResult<T>(
    ListenBrainzOutcome Outcome, T? Value, int? Status = null, string? Message = null)
{
    public bool IsOk => Outcome == ListenBrainzOutcome.Ok;

    public override string ToString() =>
        IsOk ? "ok" : $"{Outcome} (HTTP {Status?.ToString() ?? "none"}: {Message})";
}

/// <summary>The three listen types submit-listens takes.</summary>
public enum ListenType
{
    /// <summary>What plays now; not stored, no timestamp, one entry.</summary>
    PlayingNow,

    /// <summary>One finished play.</summary>
    Single,

    /// <summary>Several finished plays at once (the offline queue).</summary>
    Import,
}

internal sealed class ValidateTokenDto
{
    [JsonPropertyName("valid")] public bool? Valid { get; set; }
    [JsonPropertyName("user_name")] public string? UserName { get; set; }
}

internal sealed class ErrorDto
{
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

internal sealed class SubmitDto
{
    [JsonPropertyName("listen_type")] public required string ListenType { get; set; }
    [JsonPropertyName("payload")] public required List<ListenDto> Payload { get; set; }
}

internal sealed class ListenDto
{
    [JsonPropertyName("listened_at")] public long? ListenedAt { get; set; }
    [JsonPropertyName("track_metadata")] public required TrackMetadataDto TrackMetadata { get; set; }
}

internal sealed class TrackMetadataDto
{
    [JsonPropertyName("artist_name")] public required string ArtistName { get; set; }
    [JsonPropertyName("track_name")] public required string TrackName { get; set; }
    [JsonPropertyName("release_name")] public string? ReleaseName { get; set; }
    [JsonPropertyName("additional_info")] public required AdditionalInfoDto AdditionalInfo { get; set; }
}

internal sealed class AdditionalInfoDto
{
    [JsonPropertyName("media_player")] public required string MediaPlayer { get; set; }
    [JsonPropertyName("submission_client")] public required string SubmissionClient { get; set; }
    [JsonPropertyName("submission_client_version")] public required string SubmissionClientVersion { get; set; }
    [JsonPropertyName("duration_ms")] public long? DurationMs { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ValidateTokenDto))]
[JsonSerializable(typeof(ErrorDto))]
[JsonSerializable(typeof(SubmitDto))]
internal sealed partial class ListenBrainzJsonContext : JsonSerializerContext;
