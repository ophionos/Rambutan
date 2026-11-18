namespace Rambutan.Integrations.Scrobbling;

/// <summary>
/// One play as a scrobbling service receives it. Also the on-disk shape of the
/// offline queue, so its property names are part of <c>scrobble-queue.json</c>
/// version 1 (and of the ListenBrainz queue file).
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
