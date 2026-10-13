using System.Globalization;
using Rambutan.Lyrics.Models;

namespace Rambutan.Lyrics.Parsing;

/// <summary>
/// Reads the synced lines of a Lyricsfile 1.0 document (LRCLIB's word-level
/// format, https://lrclib.net/lyricsfile; a YAML draft spec) into a
/// <see cref="LyricsDocument"/>. Returns null for anything it can't use: a
/// version other than "1.0" (the spec forbids reading one as 1.0), YAML outside
/// <see cref="YamlSubset"/>, no synced lines, or a line missing its text or start.
/// The caller then falls back to the record's LRC, so a bad file costs the word
/// timings, never the lyrics.
/// </summary>
/// <remarks>
/// The result keeps <see cref="LrcParser"/>'s guarantees: lines in start order
/// (a stable sort, so of two equal starts the later one stays later), each line
/// ending where the next starts, words in reading order and never ending before
/// they start. The file's own line ends are not used, except to close the last
/// line, which LRC can't. <c>offset_ms</c> is ignored: the spec leaves its
/// meaning open and tells writers to leave it out. A line whose words don't
/// spell its text keeps the text and loses the words, since the karaoke sweep
/// lays the words over the text.
/// </remarks>
public static class LyricsfileParser
{
    public static LyricsDocument? Parse(string text, string source)
    {
        object? root;
        try
        {
            root = YamlSubset.Parse(text);
        }
        catch (FormatException)
        {
            return null;
        }

        if (root is not Dictionary<string, object?> file ||
            !file.TryGetValue("version", out var version) || version is not "1.0" ||
            !file.TryGetValue("lines", out var linesValue) || linesValue is not List<object?> { Count: > 0 } items)
        {
            return null;
        }

        var parsed = new List<(LyricLine Line, TimeSpan? FileEnd)>(items.Count);
        foreach (var item in items)
        {
            if (ReadLine(item) is not { } line)
                return null;
            parsed.Add(line);
        }

        var ordered = parsed.OrderBy(l => l.Line.Start).ToList();
        var lines = new List<LyricLine>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var (line, fileEnd) = ordered[i];
            var end = i + 1 < ordered.Count ? ordered[i + 1].Line.Start : fileEnd;
            lines.Add(line with { End = EndOrOpen(line.Start, end), Words = CloseWords(line, end) });
        }

        return new LyricsDocument
        {
            Source = source,
            IsSynced = true,
            Lines = lines,
        };
    }

    /// <summary>One line and the end the file gives it; null when it lacks the
    /// required text or start.</summary>
    private static (LyricLine Line, TimeSpan? FileEnd)? ReadLine(object? item)
    {
        if (item is not Dictionary<string, object?> map ||
            !map.TryGetValue("text", out var textValue) || textValue is not string text ||
            Milliseconds(map, "start_ms") is not { } start)
        {
            return null;
        }

        var fileEnd = Milliseconds(map, "end_ms");
        var words = map.TryGetValue("words", out var wordsValue) && wordsValue is List<object?> { Count: > 0 } wordItems
            ? ReadWords(wordItems, text)
            : null;
        return (new LyricLine(text.Trim(), start, Words: words), fileEnd);
    }

    /// <summary>The words, or null when any is malformed or together they don't
    /// spell <paramref name="lineText"/>.</summary>
    private static List<LyricWord>? ReadWords(List<object?> items, string lineText)
    {
        var raw = new List<(string Text, TimeSpan Start, TimeSpan? End)>(items.Count);
        foreach (var item in items)
        {
            if (item is not Dictionary<string, object?> map ||
                !map.TryGetValue("text", out var textValue) || textValue is not string text ||
                Milliseconds(map, "start_ms") is not { } start)
            {
                return null;
            }
            raw.Add((text, start, Milliseconds(map, "end_ms")));
        }

        if (string.Concat(raw.Select(w => w.Text)).Trim() != lineText.Trim())
            return null;

        // A missing end is the next word's start, as in enhanced LRC; the last
        // word's is settled when the line's end is (CloseWords).
        var words = new List<LyricWord>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var (text, start, end) = raw[i];
            if (text.Length == 0)
                continue;
            end ??= i + 1 < raw.Count ? raw[i + 1].Start : null;
            words.Add(new LyricWord(text, start, EndOrOpen(start, end)));
        }
        return words.Count > 0 ? words : null;
    }

    private static IReadOnlyList<LyricWord>? CloseWords(LyricLine line, TimeSpan? lineEnd)
    {
        if (line.Words is not { Count: > 0 } words || words[^1].End is not null || lineEnd is null)
            return line.Words;

        var closed = words.ToList();
        closed[^1] = closed[^1] with { End = EndOrOpen(closed[^1].Start, lineEnd) };
        return closed;
    }

    /// <summary>A non-negative whole number of milliseconds, or null when absent
    /// or anything else.</summary>
    private static TimeSpan? Milliseconds(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) && value is string digits &&
        long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
            ? TimeSpan.FromMilliseconds(ms)
            : null;

    /// <summary>An end, or null when it precedes its start (as <see cref="LrcParser"/>).</summary>
    private static TimeSpan? EndOrOpen(TimeSpan start, TimeSpan? end) =>
        end is not null && end.Value >= start ? end : null;
}
