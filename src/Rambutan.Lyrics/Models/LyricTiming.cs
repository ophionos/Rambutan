namespace Rambutan.Lyrics.Models;

/// <summary>
/// How a playback position reads against lyric timings: one rule set for lines
/// and words.
/// </summary>
/// <remarks>
/// A span is reached at its start (<c>Start &lt;= t</c>). A span whose end equals
/// its start (equal word stamps, or lines collapsed onto zero by a clamped
/// <c>[offset:]</c>) is complete the moment it is reached, and so is a span with
/// no end. Nothing divides by zero and no word is skipped unlit.
/// </remarks>
public static class LyricTiming
{
    /// <summary>
    /// The index of the last start at or before <paramref name="position"/>, or
    /// -1 before the first. <paramref name="starts"/> must be sorted, as a synced
    /// document's line starts are. A line stays active until the next one starts,
    /// so a blank (gap) line takes over at its own stamp and the final line holds
    /// to the end. With equal starts the later line wins: the earlier one is
    /// never active, though it stays on screen and seekable.
    /// </summary>
    public static int FindActiveLine(IReadOnlyList<TimeSpan> starts, TimeSpan position)
    {
        int lo = 0, hi = starts.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (starts[mid] <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    /// <summary>
    /// How much of a word is sung at <paramref name="position"/>: 0 before its
    /// start, 1 at or after its end, linear between. A zero-length or open word
    /// is 1 as soon as it is reached.
    /// </summary>
    /// <remarks>
    /// Words are in reading order, not time order (the parser never sorts them),
    /// so each word is read on its own; callers must not assume the sung part of
    /// a line is a prefix of it.
    /// </remarks>
    public static double Progress(LyricWord word, TimeSpan position)
    {
        if (position < word.Start)
            return 0;

        if (word.End is not { } end || end <= word.Start || position >= end)
            return 1;

        return (position - word.Start) / (end - word.Start);
    }
}
