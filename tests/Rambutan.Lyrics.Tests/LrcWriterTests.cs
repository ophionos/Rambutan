using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Parsing;

namespace Rambutan.Lyrics.Tests;

/// <summary>Found lyrics written back out as LRC, for the custom lyrics editor.</summary>
public class LrcWriterTests
{
    [Fact]
    public void Synced_lines_round_trip_through_the_parser()
    {
        const string lrc = "[00:01.00]Streetlights\n[00:04.50]\n[01:02.25]夜の街に\n";
        var doc = LrcParser.Parse(lrc, "p");

        var written = LrcWriter.Write(doc);

        Assert.Equal(lrc, written);
        Assert.Equal(doc.Lines, LrcParser.Parse(written, "p").Lines);
    }

    [Fact]
    public void Word_stamps_are_kept()
    {
        var doc = LrcParser.Parse("[00:01.00]<00:01.00>Say <00:01.50>it <00:02.00>once", "p");

        var written = LrcWriter.Write(doc);

        Assert.Equal("[00:01.00]<00:01.00>Say <00:01.50>it <00:02.00>once\n", written);
        Assert.Equal(doc.Lines[0].Words!, LrcParser.Parse(written, "p").Lines[0].Words!);
    }

    [Fact]
    public void The_offset_is_already_in_the_times_so_no_tag_is_written()
    {
        var doc = LrcParser.Parse("[offset:500]\n[00:02.00]Sooner", "p");

        Assert.Equal("[00:01.50]Sooner\n", LrcWriter.Write(doc));
    }

    [Fact]
    public void Unsynced_lyrics_come_out_as_plain_lines()
    {
        var doc = LyricsDocument.FromPlainText("One\n\nTwo", "p");

        Assert.Equal("One\n\nTwo\n", LrcWriter.Write(doc));
    }

    [Theory]
    [InlineData(0, "00:00.00")]
    [InlineData(1234, "00:01.23")]
    [InlineData(1235, "00:01.24")]
    [InlineData(59_999, "01:00.00")]
    [InlineData(3_723_450, "62:03.45")]
    public void Stamps_round_to_hundredths(int ms, string expected) =>
        Assert.Equal(expected, LrcWriter.Stamp(TimeSpan.FromMilliseconds(ms)));
}
