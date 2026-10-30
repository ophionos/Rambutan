using Rambutan.Lyrics.Models;

namespace Rambutan.Lyrics.Providers;

public enum LyricsStatus
{
    /// <summary>Lyrics were found; <see cref="LyricsLookup.Document"/> says whether they're synced.</summary>
    Found,

    /// <summary>A source says the track has no lyrics.</summary>
    Instrumental,

    /// <summary>Every source answered and none had the track.</summary>
    NotFound,

    /// <summary>
    /// No source had the track and at least one couldn't be asked (offline, timed
    /// out, erroring). Not cached: trying again may find lyrics.
    /// </summary>
    Unavailable,

    /// <summary>
    /// No custom or cached lyrics, and looking up online is switched off, so no provider was asked. Not cached: switching lookups on may
    /// find lyrics.
    /// </summary>
    NotLookedUp,
}

/// <summary>The outcome of looking up one track, with each state the pane must show.</summary>
public sealed record LyricsLookup(LyricsStatus Status, LyricsDocument? Document = null)
{
    /// <summary>False for a <see cref="LyricsStatus.NotFound"/> where no source
    /// had anything to ask for the track (<see cref="LyricsResult.IsNotAsked"/>):
    /// not cached.</summary>
    public bool Asked { get; init; } = true;

    public static LyricsLookup NotFound { get; } = new(LyricsStatus.NotFound);
    public static LyricsLookup NotAsked { get; } = new(LyricsStatus.NotFound) { Asked = false };
    public static LyricsLookup Unavailable { get; } = new(LyricsStatus.Unavailable);
    public static LyricsLookup NotLookedUp { get; } = new(LyricsStatus.NotLookedUp);

    public static LyricsLookup From(LyricsResult result) => result.IsInstrumental
        ? new(LyricsStatus.Instrumental, result.Document)
        : new(LyricsStatus.Found, result.Document);
}
