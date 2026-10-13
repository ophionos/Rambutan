namespace Rambutan.Lyrics.Providers;

public interface ILyricsProvider
{
    /// <summary>Short stable name, recorded as <see cref="Models.LyricsDocument.Source"/>.</summary>
    string Name { get; }

    /// <summary>
    /// Looks the track up. Returns null when the source doesn't have it, and
    /// throws (any exception but cancellation of <paramref name="ct"/>) when the
    /// source couldn't be asked, so the chain can tell "no lyrics exist" from
    /// "we don't know yet".
    /// </summary>
    Task<LyricsResult?> TryGetAsync(LyricsQuery query, CancellationToken ct);
}
