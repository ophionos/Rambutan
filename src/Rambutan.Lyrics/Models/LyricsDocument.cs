namespace Rambutan.Lyrics.Models;

/// <summary>
/// A single timed word within a line (enhanced LRC "A2" word stamps).
/// </summary>
/// <param name="Text">
/// The word as written, including any trailing space, so a line's text can be
/// reconstructed by concatenating its words.
/// </param>
/// <param name="End">
/// When the word stops being active — the next word's start, or the line's end
/// for the final word. Null when neither is known. Never earlier than
/// <paramref name="Start"/>: a backwards stamp in a malformed file yields null
/// rather than a negative interval, so a karaoke renderer dividing by
/// (End − Start) only has to guard the open case it already handles.
/// </param>
public sealed record LyricWord(string Text, TimeSpan Start, TimeSpan? End = null);

/// <param name="Text">
/// The line's text, with timing tags stripped. May be empty: a stamped but
/// blank line is a legitimate instrumental gap and is preserved so the previous
/// line gets a correct <see cref="End"/>.
/// </param>
/// <param name="Start">
/// When the line becomes active. Meaningful only when the owning document's
/// <see cref="LyricsDocument.IsSynced"/> is true; otherwise it is
/// <see cref="TimeSpan.Zero"/> filler.
/// </param>
/// <param name="End">
/// The next line's start, or for the final line the end its source states
/// (a Lyricsfile can; LRC can't), else null — a pure parser has no track
/// duration to close it with.
/// </param>
/// <param name="Words">Word timings when the source had them (enhanced LRC or a
/// word-synced Lyricsfile), else null.</param>
public sealed record LyricLine(
    string Text,
    TimeSpan Start,
    TimeSpan? End = null,
    IReadOnlyList<LyricWord>? Words = null);

/// <summary>
/// Parsed lyrics for one track. Times are playback-ready: any
/// <c>[offset:]</c> tag has already been applied to <see cref="Lines"/>.
/// </summary>
public sealed record LyricsDocument
{
    /// <summary>
    /// Where these lyrics came from, for display and cache bookkeeping — a
    /// provider name such as "lrclib" or "custom". A string rather than an enum
    /// so adding a provider does not churn the serialized cache format.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// True when <see cref="LyricLine.Start"/> is meaningful. False for plain
    /// unsynced lyrics, which render as scrollable text.
    /// </summary>
    public required bool IsSynced { get; init; }

    public required IReadOnlyList<LyricLine> Lines { get; init; }

    /// <summary>
    /// The source's own line saying where the text came from, as it wrote it
    /// (YouTube Music's "Source: Musixmatch"), shown beside the source's name;
    /// null when it gave none.
    /// </summary>
    public string? Credit { get; init; }

    /// <summary>
    /// The <c>[offset:]</c> tag as written, in milliseconds, or 0 when absent.
    /// Already applied to <see cref="Lines"/>; retained only so a custom-lyrics
    /// edit can round-trip the tag back out.
    /// </summary>
    public int OffsetMs { get; init; }

    /// <summary>
    /// Closes the last line, and its last word, at the end of the track, so the
    /// final words can sweep (the parser leaves them open: it has no duration).
    /// Unchanged when unsynced, already closed, or when the track ends before the
    /// last line starts; a word whose start is past the end stays open rather
    /// than ending before it starts.
    /// </summary>
    public LyricsDocument ClosedAt(TimeSpan trackEnd)
    {
        if (!IsSynced || Lines.Count == 0 || Lines[^1] is not { End: null } last || trackEnd < last.Start)
        {
            return this;
        }

        var words = last.Words;
        if (words is { Count: > 0 } && words[^1].End is null && trackEnd >= words[^1].Start)
        {
            var closedWords = words.ToList();
            closedWords[^1] = closedWords[^1] with { End = trackEnd };
            words = closedWords;
        }

        var lines = Lines.ToList();
        lines[^1] = last with { End = trackEnd, Words = words };
        return this with { Lines = lines };
    }

    public static LyricsDocument Empty(string source) => new()
    {
        Source = source,
        IsSynced = false,
        Lines = [],
    };

    /// <summary>
    /// Wraps plain unsynced lyrics. Line starts are zero filler — callers must
    /// honour <see cref="IsSynced"/> rather than trusting them.
    /// </summary>
    public static LyricsDocument FromPlainText(string text, string source)
    {
        // Same reason LrcParser.Parse strips it: a UTF-8 BOM is not whitespace to
        // char.IsWhiteSpace, so trimming leaves it glued to the first line. Here it
        // surfaces as a stray zero-width char at the head of the lyrics rather than
        // costing a line. File.ReadAllText strips it; bytes off the wire keep it.
        var lines = text
            .TrimStart('\uFEFF')
            .Split('\n')
            .Select(l => new LyricLine(l.TrimEnd('\r').TrimEnd(), TimeSpan.Zero))
            .ToList();

        // Trailing blank lines are file padding, not content.
        while (lines.Count > 0 && lines[^1].Text.Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return new LyricsDocument
        {
            Source = source,
            IsSynced = false,
            Lines = lines,
        };
    }
}
