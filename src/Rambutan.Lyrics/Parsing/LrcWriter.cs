using System.Globalization;
using System.Text;
using Rambutan.Lyrics.Models;

namespace Rambutan.Lyrics.Parsing;

/// <summary>
/// <see cref="LyricsDocument"/> → LRC text: the starting point when the user
/// edits lyrics a provider found. Synced lines get line stamps, and word
/// stamps where the document has word timings; unsynced lyrics come out as plain
/// lines. Times are written as the document holds them, with any offset already
/// applied, so no <c>[offset:]</c> tag is written. Stamps are rounded to
/// hundredths, the precision LRC files carry.
/// </summary>
public static class LrcWriter
{
    public static string Write(LyricsDocument document)
    {
        var text = new StringBuilder();
        foreach (var line in document.Lines)
        {
            if (!document.IsSynced)
            {
                text.Append(line.Text).Append('\n');
                continue;
            }

            text.Append('[').Append(Stamp(line.Start)).Append(']');
            if (line.Words is { Count: > 0 } words)
            {
                foreach (var word in words)
                    text.Append('<').Append(Stamp(word.Start)).Append('>').Append(word.Text);
            }
            else
            {
                text.Append(line.Text);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary><c>mm:ss.xx</c>, minutes past 59 included, as the parser reads it.</summary>
    public static string Stamp(TimeSpan at)
    {
        var hundredths = (long)Math.Round(Math.Max(0, at.TotalMilliseconds) / 10, MidpointRounding.AwayFromZero);
        var minutes = hundredths / 6000;
        var seconds = hundredths / 100 % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{seconds:00}.{hundredths % 100:00}");
    }
}
