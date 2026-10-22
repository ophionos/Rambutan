using System.Buffers.Binary;
using System.Text;
using Rambutan.Lyrics.Models;

namespace Rambutan.Lyrics.Parsing;

/// <summary>
/// Synced lyrics from an ID3v2.3 or 2.4 tag's <c>SYLT</c> frame, which libav
/// doesn't expose. Only frames of lyrics (content type 1) or a transcription (2)
/// timed in milliseconds are read: a time in MPEG frames needs the audio's
/// frame length, and taggers write milliseconds. The lyrics are line-level: an
/// event is a line, unless some events start with a line break; then those
/// start the lines, and the events between are syllables of the line they're in.
/// </summary>
public static class SyltParser
{
    private const int HeaderLength = 10;
    private const byte TimeInMilliseconds = 2;

    /// <summary>The tag's length, header and footer included, from its first
    /// <see cref="HeaderLength"/> bytes; 0 when they aren't an ID3v2 header.</summary>
    public static int TagLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderLength || !header.StartsWith("ID3"u8))
            return 0;
        // Flag 0x10 adds a 10-byte footer.
        return HeaderLength + Synchsafe(header[6..10]) + ((header[5] & 0x10) != 0 ? HeaderLength : 0);
    }

    /// <summary>The first usable <c>SYLT</c> frame in <paramref name="tag"/> (the
    /// whole tag, header first) as synced lyrics; null when there is none. Never
    /// throws on malformed bytes.</summary>
    public static LyricsDocument? Parse(ReadOnlySpan<byte> tag, string source)
    {
        if (TagLength(tag) == 0)
            return null;
        var major = tag[3];
        if (major is not (3 or 4))
            return null;
        var flags = tag[5];
        var body = tag[HeaderLength..Math.Min(tag.Length, HeaderLength + Synchsafe(tag[6..10]))];

        // 2.3 unsynchronises the whole tag; 2.4 marks each frame.
        if (major == 3 && (flags & 0x80) != 0)
            body = Resync(body);

        var position = 0;
        if ((flags & 0x40) != 0 && body.Length >= 4)
            position = major == 3 ? 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(body) : Synchsafe(body[..4]);

        while (position >= 0 && position + HeaderLength <= body.Length && body[position] != 0)
        {
            var id = body.Slice(position, 4);
            var size = major == 4 ? Synchsafe(body.Slice(position + 4, 4)) : (int)BinaryPrimitives.ReadUInt32BigEndian(body[(position + 4)..]);
            var format = body[position + 9];
            var start = position + HeaderLength;
            if (size < 0 || size > body.Length - start)
                return null;
            position = start + size;
            if (!id.SequenceEqual("SYLT"u8) || FrameData(body.Slice(start, size), major, format) is not { } data)
                continue;
            if (Lyrics(data, source) is { } lyrics)
                return lyrics;
        }
        return null;
    }

    /// <summary>The frame's data with its format flags undone; null for a frame
    /// compressed or encrypted, which this doesn't read.</summary>
    private static byte[]? FrameData(ReadOnlySpan<byte> frame, byte major, byte format)
    {
        if (major == 3)
        {
            if ((format & 0xC0) != 0)
                return null;
            return ((format & 0x20) != 0 ? frame[Math.Min(1, frame.Length)..] : frame).ToArray();
        }
        if ((format & 0x0C) != 0)
            return null;
        var skip = ((format & 0x40) != 0 ? 1 : 0) + ((format & 0x01) != 0 ? 4 : 0);
        var data = frame[Math.Min(skip, frame.Length)..];
        return (format & 0x02) != 0 ? Resync(data) : data.ToArray();
    }

    private static LyricsDocument? Lyrics(ReadOnlySpan<byte> data, string source)
    {
        // Encoding, language (3), time format, content type, then a description.
        if (data.Length < 6 || data[4] != TimeInMilliseconds || data[5] is not (1 or 2))
            return null;
        var encoding = data[0];
        if (encoding > 3)
            return null;
        var rest = data[6..];
        if (ReadText(ref rest, encoding) is null)
            return null;

        var events = new List<(TimeSpan At, string Text)>();
        while (rest.Length > 0 && ReadText(ref rest, encoding) is { } text && rest.Length >= 4)
        {
            events.Add((TimeSpan.FromMilliseconds(BinaryPrimitives.ReadUInt32BigEndian(rest)), text));
            rest = rest[4..];
        }
        return Document(events, source);
    }

    private static LyricsDocument? Document(List<(TimeSpan At, string Text)> events, string source)
    {
        var syllables = events.Skip(1).Any(e => e.Text.StartsWith('\n') || e.Text.StartsWith('\r'));
        var lines = new List<(TimeSpan Start, StringBuilder Text)>();
        foreach (var (at, text) in events)
        {
            var startsLine = !syllables || lines.Count == 0 || text.StartsWith('\n') || text.StartsWith('\r');
            if (startsLine)
                lines.Add((at, new StringBuilder()));
            lines[^1].Text.Append(text.Replace("\r", "").Replace("\n", ""));
        }
        if (lines.All(l => l.Text.ToString().Trim().Length == 0))
            return null;

        // Events are stored in time order, but a broken writer needn't keep it.
        var ordered = lines.OrderBy(l => l.Start).ToList();
        return new LyricsDocument
        {
            Source = source,
            IsSynced = true,
            Lines = ordered.Select((l, i) => new LyricLine(
                l.Text.ToString().Trim(), l.Start, i + 1 < ordered.Count ? ordered[i + 1].Start : null)).ToList(),
        };
    }

    /// <summary>A text ended by its terminator (or the data's end), in the frame's
    /// encoding; moves <paramref name="data"/> past it. Null when nothing is left.</summary>
    private static string? ReadText(ref ReadOnlySpan<byte> data, byte encoding)
    {
        if (data.IsEmpty)
            return null;
        var wide = encoding is 1 or 2;
        var end = 0;
        if (wide)
        {
            while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0))
                end += 2;
        }
        else
        {
            end = data.IndexOf((byte)0);
            if (end < 0)
                end = data.Length;
        }
        var bytes = data[..Math.Min(end, data.Length)];
        data = data[Math.Min(data.Length, end + (wide ? 2 : 1))..];
        return encoding switch
        {
            0 => Encoding.Latin1.GetString(bytes),
            1 => Utf16(bytes),
            2 => Encoding.BigEndianUnicode.GetString(bytes),
            _ => Encoding.UTF8.GetString(bytes),
        };
    }

    /// <summary>UTF-16 after its byte order mark; little-endian without one, as
    /// writers that leave it out mean.</summary>
    private static string Utf16(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) ? Encoding.BigEndianUnicode.GetString(bytes[2..])
        : bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) ? Encoding.Unicode.GetString(bytes[2..])
        : Encoding.Unicode.GetString(bytes);

    /// <summary>Undoes unsynchronisation: a 0x00 written after each 0xFF.</summary>
    private static byte[] Resync(ReadOnlySpan<byte> data)
    {
        var result = new List<byte>(data.Length);
        for (var i = 0; i < data.Length; i++)
        {
            result.Add(data[i]);
            if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0x00)
                i++;
        }
        return [.. result];
    }

    /// <summary>Four 7-bit bytes, most significant first.</summary>
    private static int Synchsafe(ReadOnlySpan<byte> bytes) =>
        (bytes[0] & 0x7f) << 21 | (bytes[1] & 0x7f) << 14 | (bytes[2] & 0x7f) << 7 | (bytes[3] & 0x7f);
}
