using Rambutan.Lyrics.Models;

namespace Rambutan.Lyrics.Providers;

/// <summary>
/// A provider's positive answer: lyrics, or an explicit statement that the
/// track has none. A provider that simply doesn't know the track returns null
/// instead, and one that couldn't be asked throws.
/// </summary>
public sealed record LyricsResult
{
    public required LyricsDocument Document { get; init; }

    /// <summary>
    /// The source says the track is instrumental. <see cref="Document"/> is then
    /// empty. This is an answer, not a miss: the pane shows "instrumental" rather
    /// than "no lyrics found", and the next provider isn't asked.
    /// </summary>
    public bool IsInstrumental { get; init; }

    public static LyricsResult Found(LyricsDocument document) => new() { Document = document };

    public static LyricsResult Instrumental(string source) => new()
    {
        Document = LyricsDocument.Empty(source),
        IsInstrumental = true,
    };
}
