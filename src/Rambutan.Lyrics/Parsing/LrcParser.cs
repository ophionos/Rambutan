using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rambutan.Lyrics.Models;

namespace Rambutan.Lyrics.Parsing;

/// <summary>
/// Pure text → <see cref="LyricsDocument"/> for plain and enhanced ("A2") LRC.
/// No I/O, no state. Malformed input degrades line by line and never throws:
/// anything that fails to match is skipped, so one broken stamp does not cost
/// the rest of the file.
/// </summary>
public static partial class LrcParser
{
    /// <summary>
    /// Line stamp: <c>[mm:ss.xx]</c>, also accepting <c>mm:ss</c>, three-digit
    /// milliseconds, and a colon before the fraction (<c>[mm:ss:xx]</c>), all of
    /// which appear in the wild. Minutes may exceed 59.
    /// </summary>
    [GeneratedRegex(@"\[(\d{1,3}):([0-5]?\d)(?:[.:](\d{1,3}))?\]")]
    private static partial Regex LineStampRegex { get; }

    /// <summary>Enhanced-LRC word stamp: <c>&lt;mm:ss.xx&gt;</c>.</summary>
    [GeneratedRegex(@"<(\d{1,3}):([0-5]?\d)(?:[.:](\d{1,3}))?>")]
    private static partial Regex WordStampRegex { get; }

    /// <summary>A metadata tag such as <c>[ti:Title]</c> — letters, then a colon.</summary>
    [GeneratedRegex(@"^\[([a-zA-Z]+):(.*)\]$")]
    private static partial Regex MetadataTagRegex { get; }

    /// <summary>
    /// Parses LRC text. Returns an unsynced empty document when the input holds
    /// no usable stamps, so callers can fall back to plain-text handling.
    /// </summary>
    /// <param name="extraOffsetMs">
    /// A further adjustment applied the same way as the file's own
    /// <c>[offset:]</c> tag (positive makes lyrics appear sooner): the user's
    /// correction on custom lyrics. <see cref="LyricsDocument.OffsetMs"/> still
    /// records only the tag, so the file round-trips as written.
    /// </param>
    public static LyricsDocument Parse(string text, string source, int extraOffsetMs = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return LyricsDocument.Empty(source);
        }

        // A UTF-8 BOM is not whitespace to char.IsWhiteSpace, so Trim() below
        // would leave it glued to the first stamp and cost us the first line.
        // File.ReadAllText strips it, but bytes off the wire keep it.
        text = text.TrimStart('\uFEFF');

        var offsetMs = 0;
        var parsed = new List<LyricLine>();

        foreach (var rawLine in text.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var metadata = MetadataTagRegex.Match(trimmed);
            if (metadata.Success)
            {
                if (metadata.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(
                        metadata.Groups[2].Value.Trim(),
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var parsedOffset))
                {
                    offsetMs = parsedOffset;
                }

                // Every other tag ([ti:], [ar:], [al:], [by:], [length:], …) is
                // catalogued elsewhere; the player only needs times and text.
                continue;
            }

            ReadLine(trimmed, parsed);
        }

        if (parsed.Count == 0)
        {
            return LyricsDocument.Empty(source);
        }

        // A positive [offset:] makes lyrics appear sooner, so it subtracts from
        // every stamp (per the de-facto LRC spec; sources disagree, this is the
        // documented one). Clamped at zero — a shift can't precede the track.
        var shift = TimeSpan.FromMilliseconds((long)offsetMs + extraOffsetMs);
        var ordered = parsed
            .Select(l => ShiftLine(l, shift))
            .OrderBy(l => l.Start)
            .ToList();

        return new LyricsDocument
        {
            Source = source,
            IsSynced = true,
            Lines = CloseIntervals(ordered),
            OffsetMs = offsetMs,
        };
    }

    /// <summary>
    /// Reads one non-metadata line into zero or more <see cref="LyricLine"/>s —
    /// several when the line carries repeated stamps
    /// (<c>[00:12.00][00:15.00] text</c>), which share the text at each time.
    /// </summary>
    private static void ReadLine(string trimmed, List<LyricLine> into)
    {
        var stamps = new List<TimeSpan>();
        var lastStampEnd = 0;

        foreach (Match match in LineStampRegex.Matches(trimmed))
        {
            // Stamps only count as timing while they form an unbroken run at the
            // head of the line; a "[00:12.00]" inside the lyric text is content.
            if (match.Index != lastStampEnd)
            {
                break;
            }

            stamps.Add(ToTimeSpan(match));
            lastStampEnd = match.Index + match.Length;
        }

        if (stamps.Count == 0)
        {
            return;
        }

        var body = trimmed[lastStampEnd..];

        // Word timings are only well defined for a single-stamp line; a repeated
        // stamp would need the words replayed per occurrence, which no writer emits.
        if (stamps.Count == 1)
        {
            var words = ReadWords(body, stamps[0]);
            if (words is not null)
            {
                var text = string.Concat(words.Select(w => w.Text)).Trim();
                into.Add(new LyricLine(text, stamps[0], Words: words));
                return;
            }
        }

        var plain = WordStampRegex.Replace(body, string.Empty).Trim();
        foreach (var stamp in stamps)
        {
            into.Add(new LyricLine(plain, stamp));
        }
    }

    /// <summary>
    /// Splits a line body on its <c>&lt;mm:ss.xx&gt;</c> stamps. Returns null
    /// when the line carries none, i.e. it is plain rather than enhanced LRC.
    /// </summary>
    private static List<LyricWord>? ReadWords(string body, TimeSpan lineStart)
    {
        var matches = WordStampRegex.Matches(body);
        if (matches.Count == 0)
        {
            return null;
        }

        var words = new List<LyricWord>(matches.Count + 1);

        // Text before the first stamp belongs to the line's own start.
        var lead = body[..matches[0].Index];
        if (lead.Trim().Length > 0)
        {
            words.Add(new LyricWord(lead, lineStart, EndOrOpen(lineStart, ToTimeSpan(matches[0]))));
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var start = ToTimeSpan(matches[i]);
            var textStart = matches[i].Index + matches[i].Length;
            var textEnd = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;

            var wordText = body[textStart..textEnd];
            if (wordText.Length == 0)
            {
                continue;
            }

            var end = i + 1 < matches.Count ? ToTimeSpan(matches[i + 1]) : (TimeSpan?)null;
            words.Add(new LyricWord(wordText, start, EndOrOpen(start, end)));
        }

        return words.Count > 0 ? words : null;
    }

    /// <summary>
    /// A word's end, or null when the candidate precedes its start.
    /// </summary>
    /// <remarks>
    /// Unlike lines, words are never reordered: a line's words are its reading
    /// order, and sorting them by stamp would rewrite the lyric. So a backwards
    /// stamp yields an open interval rather than a negative one — "end unknown",
    /// the state the model already documents and which every consumer must
    /// already handle for the last word of the last line.
    /// </remarks>
    private static TimeSpan? EndOrOpen(TimeSpan start, TimeSpan? end) =>
        end is not null && end.Value >= start ? end : null;

    /// <summary>Applies the file offset to a line and its words, clamping at zero.</summary>
    private static LyricLine ShiftLine(LyricLine line, TimeSpan shift)
    {
        if (shift == TimeSpan.Zero)
        {
            return line;
        }

        var words = line.Words?
            .Select(w => new LyricWord(w.Text, Shift(w.Start, shift), Shift(w.End, shift)))
            .ToList();

        return line with { Start = Shift(line.Start, shift), Words = words };
    }

    private static TimeSpan Shift(TimeSpan value, TimeSpan shift)
    {
        var shifted = value - shift;
        return shifted < TimeSpan.Zero ? TimeSpan.Zero : shifted;
    }

    private static TimeSpan? Shift(TimeSpan? value, TimeSpan shift) =>
        value is null ? null : Shift(value.Value, shift);

    /// <summary>
    /// Closes each line's interval with the next line's start, and each final
    /// word's interval with its line's end. The last line stays open — a pure
    /// parser has no track duration to close it with.
    /// </summary>
    private static List<LyricLine> CloseIntervals(List<LyricLine> ordered)
    {
        var closed = new List<LyricLine>(ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            var line = ordered[i];
            var end = i + 1 < ordered.Count ? ordered[i + 1].Start : (TimeSpan?)null;

            var words = line.Words;
            if (words is { Count: > 0 } && words[^1].End is null && end is not null)
            {
                // A word stamp can run past the next line's start in a malformed
                // file; EndOrOpen keeps that from closing the word backwards.
                var closedWords = words.ToList();
                closedWords[^1] = closedWords[^1] with
                {
                    End = EndOrOpen(closedWords[^1].Start, end),
                };
                words = closedWords;
            }

            closed.Add(line with { End = end, Words = words });
        }

        return closed;
    }

    private static TimeSpan ToTimeSpan(Match match)
    {
        var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

        // ".5" is 500 ms and ".50" is 500 ms too — pad right, don't parse as-is.
        var fraction = match.Groups[3].Success ? match.Groups[3].Value : string.Empty;
        var milliseconds = fraction.Length == 0
            ? 0
            : int.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture);

        return new TimeSpan(0, 0, minutes, seconds, milliseconds);
    }
}
