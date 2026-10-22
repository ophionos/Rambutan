using System.Buffers.Binary;
using System.Text;
using Rambutan.Lyrics.Parsing;

namespace Rambutan.Lyrics.Tests;

/// <summary>Builds ID3v2 tags by hand, as the specification lays them out.</summary>
internal static class Id3
{
    public static byte[] Tag(byte major, byte flags, params byte[][] frames)
    {
        var body = frames.SelectMany(f => f).ToArray();
        return [.. "ID3"u8, major, 0, flags, .. Synchsafe(body.Length), .. body];
    }

    public static byte[] Frame(byte major, string id, byte[] data, byte format = 0)
    {
        var size = major == 4 ? Synchsafe(data.Length) : BigEndian((uint)data.Length);
        return [.. Encoding.ASCII.GetBytes(id), .. size, 0, format, .. data];
    }

    /// <summary>A SYLT frame's data: lyrics (content type 1) in milliseconds unless told otherwise.</summary>
    public static byte[] Sylt(byte encoding, (string Text, uint Ms)[] events, byte timeFormat = 2, byte content = 1) =>
        [encoding, .. "eng"u8, timeFormat, content, .. Text("", encoding),
            .. events.SelectMany(e => Text(e.Text, encoding).Concat(BigEndian(e.Ms)))];

    public static byte[] Text(string text, byte encoding) => encoding switch
    {
        0 => [.. Encoding.Latin1.GetBytes(text), 0],
        1 => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text), 0, 0],
        2 => [.. Encoding.BigEndianUnicode.GetBytes(text), 0, 0],
        _ => [.. Encoding.UTF8.GetBytes(text), 0],
    };

    public static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Synchsafe(int value) =>
        [(byte)(value >> 21 & 0x7f), (byte)(value >> 14 & 0x7f), (byte)(value >> 7 & 0x7f), (byte)(value & 0x7f)];

    /// <summary>Unsynchronisation: a 0x00 after each 0xFF.</summary>
    public static byte[] Unsync(byte[] data) => data.SelectMany(b => b == 0xFF ? new byte[] { 0xFF, 0 } : [b]).ToArray();
}

public class SyltParserTests
{
    private static readonly (string, uint)[] Two = [("First line", 1000), ("Second line", 2500)];

    [Fact]
    public void Each_event_is_a_line_with_its_time()
    {
        var tag = Id3.Tag(4, 0, Id3.Frame(4, "SYLT", Id3.Sylt(3, Two)));

        var doc = SyltParser.Parse(tag, "tags")!;

        Assert.True(doc.IsSynced);
        Assert.Equal("tags", doc.Source);
        Assert.Equal(["First line", "Second line"], doc.Lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), doc.Lines[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), doc.Lines[0].End);
        Assert.Null(doc.Lines[1].End);
        Assert.All(doc.Lines, l => Assert.Null(l.Words));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_text_encoding_reads(byte encoding)
    {
        var tag = Id3.Tag(3, 0, Id3.Frame(3, "SYLT", Id3.Sylt(encoding, [("Café", 500)])));

        Assert.Equal("Café", SyltParser.Parse(tag, "tags")!.Lines[0].Text);
    }

    [Fact]
    public void Other_frames_before_it_are_skipped()
    {
        var picture = Id3.Frame(3, "APIC", new byte[5000]);
        var uslt = Id3.Frame(3, "USLT", [3, .. "eng"u8, 0, .. "Plain"u8]);
        var tag = Id3.Tag(3, 0, picture, uslt, Id3.Frame(3, "SYLT", Id3.Sylt(1, Two)));

        Assert.Equal("First line", SyltParser.Parse(tag, "tags")!.Lines[0].Text);
    }

    [Fact]
    public void Events_that_start_with_a_line_break_start_the_lines()
    {
        (string, uint)[] syllables = [("Hel", 1000), ("lo ", 1300), ("there", 1600), ("\nSec", 3000), ("ond", 3400)];
        var tag = Id3.Tag(4, 0, Id3.Frame(4, "SYLT", Id3.Sylt(3, syllables)));

        var doc = SyltParser.Parse(tag, "tags")!;

        Assert.Equal(["Hello there", "Second"], doc.Lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(3000), doc.Lines[1].Start);
    }

    [Theory]
    [InlineData(1, 1)] // MPEG frames, not milliseconds
    [InlineData(2, 3)] // movement names, not lyrics
    public void Frames_it_cant_place_or_that_arent_lyrics_are_left_out(byte timeFormat, byte content)
    {
        var tag = Id3.Tag(4, 0, Id3.Frame(4, "SYLT", Id3.Sylt(3, Two, timeFormat, content)));

        Assert.Null(SyltParser.Parse(tag, "tags"));
    }

    [Fact]
    public void A_later_usable_frame_is_taken_after_one_that_isnt()
    {
        var chords = Id3.Frame(4, "SYLT", Id3.Sylt(3, [("Am", 0)], content: 5));
        var tag = Id3.Tag(4, 0, chords, Id3.Frame(4, "SYLT", Id3.Sylt(3, Two)));

        Assert.Equal("First line", SyltParser.Parse(tag, "tags")!.Lines[0].Text);
    }

    [Fact]
    public void A_v23_tag_is_resynchronised()
    {
        // 0x0001FF00 ms holds an 0xFF followed by 0x00, which unsynchronisation escapes.
        var frame = Id3.Frame(3, "SYLT", Id3.Sylt(3, [("Late", 0x0001FF00)]));
        var tag = Id3.Tag(3, 0x80, Id3.Unsync(frame));

        Assert.Equal(TimeSpan.FromMilliseconds(0x0001FF00), SyltParser.Parse(tag, "tags")!.Lines[0].Start);
    }

    [Fact]
    public void A_v24_frame_is_resynchronised_and_its_length_indicator_skipped()
    {
        var data = Id3.Sylt(3, [("Late", 0x0001FF00)]);
        var frame = Id3.Frame(4, "SYLT", [.. new byte[] { 0, 0, 0, (byte)data.Length }, .. Id3.Unsync(data)], format: 0x03);

        Assert.Equal("Late", SyltParser.Parse(Id3.Tag(4, 0, frame), "tags")!.Lines[0].Text);
    }

    [Fact]
    public void Compressed_frames_and_other_versions_are_left_out()
    {
        Assert.Null(SyltParser.Parse(Id3.Tag(3, 0, Id3.Frame(3, "SYLT", Id3.Sylt(3, Two), format: 0x80)), "tags"));
        Assert.Null(SyltParser.Parse(Id3.Tag(2, 0, Id3.Frame(3, "SYLT", Id3.Sylt(3, Two))), "tags"));
    }

    [Fact]
    public void Broken_bytes_give_nothing_and_never_throw()
    {
        var whole = Id3.Tag(4, 0, Id3.Frame(4, "SYLT", Id3.Sylt(3, Two)));
        for (var length = 0; length < whole.Length; length++)
            SyltParser.Parse(whole.AsSpan(0, length), "tags");

        var random = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var bytes = new byte[random.Next(10, 200)];
            random.NextBytes(bytes);
            "ID3"u8.CopyTo(bytes);
            bytes[3] = (byte)random.Next(3, 5);
            SyltParser.Parse(bytes, "tags");
        }

        var lying = Id3.Tag(4, 0, Id3.Frame(4, "SYLT", Id3.Sylt(3, Two)));
        lying[10 + 4] = 0x7f; // a frame size past the tag's end
        Assert.Null(SyltParser.Parse(lying, "tags"));
    }

    [Fact]
    public void The_tag_length_counts_the_header_and_any_footer()
    {
        var tag = Id3.Tag(4, 0, Id3.Frame(4, "SYLT", Id3.Sylt(3, Two)));
        Assert.Equal(tag.Length, SyltParser.TagLength(tag));

        tag[5] = 0x10;
        Assert.Equal(tag.Length + 10, SyltParser.TagLength(tag));
        Assert.Equal(0, SyltParser.TagLength("RIFF0000WAVE"u8));
    }
}
