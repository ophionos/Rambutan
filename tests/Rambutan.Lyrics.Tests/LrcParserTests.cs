using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Parsing;

namespace Rambutan.Lyrics.Tests;

public class LrcParserTests
{
    private const string Source = "test";

    private static LyricsDocument Parse(string text) => LrcParser.Parse(text, Source);

    private static TimeSpan At(int minutes, int seconds, int milliseconds = 0) =>
        new(0, 0, minutes, seconds, milliseconds);

    [Fact]
    public void Parses_plain_stamps_into_ordered_lines()
    {
        var doc = Parse("""
            [00:12.00]First line
            [00:15.50]Second line
            [01:02.25]Third line
            """);

        Assert.True(doc.IsSynced);
        Assert.Equal(Source, doc.Source);
        Assert.Equal(
            ["First line", "Second line", "Third line"],
            doc.Lines.Select(l => l.Text));
        Assert.Equal(
            [At(0, 12), At(0, 15, 500), At(1, 2, 250)],
            doc.Lines.Select(l => l.Start));
    }

    [Fact]
    public void Derives_end_from_next_line_and_leaves_the_last_open()
    {
        var doc = Parse("""
            [00:10.00]One
            [00:20.00]Two
            """);

        Assert.Equal(At(0, 20), doc.Lines[0].End);
        Assert.Null(doc.Lines[1].End);
    }

    [Fact]
    public void Sorts_out_of_order_stamps()
    {
        var doc = Parse("""
            [00:30.00]Last
            [00:10.00]First
            [00:20.00]Middle
            """);

        Assert.Equal(["First", "Middle", "Last"], doc.Lines.Select(l => l.Text));
        Assert.Equal(At(0, 20), doc.Lines[0].End);
    }

    [Fact]
    public void Repeated_stamps_on_one_line_become_separate_lines()
    {
        var doc = Parse("[00:12.00][00:15.00][00:40.00]Chorus");

        Assert.Equal(3, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Equal("Chorus", l.Text));
        Assert.Equal([At(0, 12), At(0, 15), At(0, 40)], doc.Lines.Select(l => l.Start));
    }

    [Fact]
    public void Accepts_stamp_variants_seen_in_the_wild()
    {
        var doc = Parse("""
            [00:01]No fraction
            [00:02.5]One digit
            [00:03.25]Centiseconds
            [00:04.125]Milliseconds
            [00:05:75]Colon fraction
            [100:00.00]Minutes past an hour
            """);

        Assert.Equal(
            [At(0, 1), At(0, 2, 500), At(0, 3, 250), At(0, 4, 125), At(0, 5, 750), At(100, 0)],
            doc.Lines.Select(l => l.Start));
    }

    [Fact]
    public void Skips_metadata_tags()
    {
        var doc = Parse("""
            [ti:Song Title]
            [ar:Some Artist]
            [al:Some Album]
            [by:Some Transcriber]
            [length:03:21]
            [00:12.00]Only real line
            """);

        var line = Assert.Single(doc.Lines);
        Assert.Equal("Only real line", line.Text);
    }

    [Fact]
    public void Positive_offset_makes_lyrics_appear_sooner()
    {
        // Per the de-facto LRC spec: a positive [offset:] advances the lyrics,
        // so it subtracts from every stamp.
        var doc = Parse("""
            [offset:500]
            [00:10.00]Line
            """);

        Assert.Equal(500, doc.OffsetMs);
        Assert.Equal(At(0, 9, 500), doc.Lines[0].Start);
    }

    [Fact]
    public void Negative_offset_delays_lyrics()
    {
        var doc = Parse("""
            [offset:-500]
            [00:10.00]Line
            """);

        Assert.Equal(-500, doc.OffsetMs);
        Assert.Equal(At(0, 10, 500), doc.Lines[0].Start);
    }

    [Fact]
    public void Offset_never_pushes_a_stamp_before_zero()
    {
        var doc = Parse("""
            [offset:5000]
            [00:01.00]Line
            """);

        Assert.Equal(TimeSpan.Zero, doc.Lines[0].Start);
    }

    [Fact]
    public void Offset_shifts_word_stamps_too()
    {
        var doc = Parse("""
            [offset:1000]
            [00:10.00]<00:10.00>Hey <00:11.00>you
            """);

        var words = doc.Lines[0].Words!;
        Assert.Equal(At(0, 9), words[0].Start);
        Assert.Equal(At(0, 10), words[1].Start);
    }

    [Fact]
    public void Parses_word_stamps()
    {
        var doc = Parse("[00:10.00]<00:10.00>Never <00:10.50>gonna <00:11.00>give");

        var line = Assert.Single(doc.Lines);
        Assert.Equal("Never gonna give", line.Text);

        var words = line.Words!;
        Assert.Equal(["Never ", "gonna ", "give"], words.Select(w => w.Text));
        Assert.Equal([At(0, 10), At(0, 10, 500), At(0, 11)], words.Select(w => w.Start));
        Assert.Equal(At(0, 10, 500), words[0].End);
        Assert.Equal(At(0, 11), words[1].End);
    }

    [Fact]
    public void Final_word_takes_its_end_from_the_line()
    {
        var doc = Parse("""
            [00:10.00]<00:10.00>Hello <00:11.00>world
            [00:20.00]Next
            """);

        var words = doc.Lines[0].Words!;
        Assert.Equal(At(0, 20), words[^1].End);
    }

    [Fact]
    public void Final_word_of_the_final_line_stays_open()
    {
        var doc = Parse("[00:10.00]<00:10.00>Hello <00:11.00>world");

        Assert.Null(doc.Lines[0].Words![^1].End);
    }

    [Fact]
    public void Text_before_the_first_word_stamp_starts_at_the_line()
    {
        var doc = Parse("[00:10.00]Lead <00:11.00>in");

        var words = doc.Lines[0].Words!;
        Assert.Equal("Lead ", words[0].Text);
        Assert.Equal(At(0, 10), words[0].Start);
        Assert.Equal(At(0, 11), words[0].End);
        Assert.Equal("Lead in", doc.Lines[0].Text);
    }

    [Fact]
    public void Plain_lines_carry_no_words()
    {
        var doc = Parse("[00:10.00]Just text");

        Assert.Null(doc.Lines[0].Words);
    }

    // Words keep reading order — sorting them the way lines are sorted would
    // rewrite the lyric — so a backwards stamp has to degrade some other way.
    // It opens the interval instead of producing End < Start, which the karaoke
    // control would divide by.

    [Fact]
    public void A_backwards_word_stamp_opens_the_interval_instead_of_inverting_it()
    {
        var doc = Parse("""
            [00:10.00]<00:12.00>alpha <00:11.00>beta
            [00:20.00]Next
            """);

        var words = doc.Lines[0].Words!;
        Assert.Equal("alpha ", words[0].Text);
        Assert.Equal(At(0, 12), words[0].Start);
        Assert.Null(words[0].End);
    }

    [Fact]
    public void Lead_text_opens_when_the_line_stamp_is_later_than_the_first_word()
    {
        var doc = Parse("""
            [00:20.00]Hey <00:12.00>you
            [00:30.00]Next
            """);

        var words = doc.Lines[0].Words!;
        Assert.Equal("Hey ", words[0].Text);
        Assert.Equal(At(0, 20), words[0].Start);
        Assert.Null(words[0].End);
    }

    [Fact]
    public void A_word_running_past_the_next_line_is_not_closed_backwards()
    {
        var doc = Parse("""
            [00:10.00]<00:15.00>held
            [00:12.00]Next
            """);

        var words = doc.Lines[0].Words!;
        Assert.Equal(At(0, 15), words[^1].Start);
        Assert.Null(words[^1].End);
    }

    [Fact]
    public void No_word_ever_ends_before_it_starts()
    {
        var doc = Parse("""
            [00:20.00]Lead <00:12.00>a<00:30.00>b<00:14.00>c
            [00:15.00]Next <00:40.00>over
            """);

        var words = doc.Lines.SelectMany(l => l.Words ?? []);
        Assert.All(words, w => Assert.True(
            w.End is null || w.End >= w.Start,
            $"'{w.Text}' runs {w.Start} -> {w.End}"));
    }

    [Fact]
    public void Word_stamps_are_stripped_when_a_line_repeats_its_stamp()
    {
        // Repeated stamps plus word timings is ill-defined; keep the text, drop
        // the word timings rather than replay them at the wrong times.
        var doc = Parse("[00:12.00][00:30.00]<00:12.00>Chorus <00:13.00>line");

        Assert.Equal(2, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Equal("Chorus line", l.Text));
        Assert.All(doc.Lines, l => Assert.Null(l.Words));
    }

    [Fact]
    public void Malformed_lines_are_skipped_without_losing_good_ones()
    {
        var doc = Parse("""
            [00:10.00]Good one
            [99:99.99]Bad seconds
            [garbled]Not a stamp
            no stamp at all
            [00:2Z.00]Bad digits
            [00:20.00]Good two
            """);

        Assert.Equal(["Good one", "Good two"], doc.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Keeps_stamped_blank_lines_as_gaps()
    {
        var doc = Parse("""
            [00:10.00]Verse
            [00:14.00]
            [00:20.00]Chorus
            """);

        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal("", doc.Lines[1].Text);
        // The gap gives the preceding line a correct end rather than letting it
        // hang until the chorus.
        Assert.Equal(At(0, 14), doc.Lines[0].End);
    }

    [Fact]
    public void A_stamp_inside_the_text_is_content_not_timing()
    {
        var doc = Parse("[00:10.00]Meet me at [00:15.00] sharp");

        var line = Assert.Single(doc.Lines);
        Assert.Equal(At(0, 10), line.Start);
        Assert.Equal("Meet me at [00:15.00] sharp", line.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n")]
    [InlineData("Just plain lyrics\nwith no timing at all")]
    [InlineData("[ti:Only metadata]")]
    public void Input_without_usable_stamps_yields_an_empty_unsynced_document(string text)
    {
        var doc = Parse(text);

        Assert.False(doc.IsSynced);
        Assert.Empty(doc.Lines);
    }

    [Fact]
    public void Handles_a_utf8_bom_without_losing_the_first_line()
    {
        // A BOM is not whitespace to char.IsWhiteSpace, so an unhandled one
        // stays glued to the first stamp and costs the first line entirely.
        var doc = Parse("\uFEFF[00:10.00]One\n[00:20.00]Two");

        Assert.Equal(["One", "Two"], doc.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Offset_is_applied_to_lines_not_merely_reported()
    {
        // Guards the apply-and-retain contract: consumers must never add
        // OffsetMs to Lines themselves, or the shift lands twice.
        var doc = Parse("""
            [offset:500]
            [00:10.00]Line
            """);

        Assert.Equal(500, doc.OffsetMs);
        Assert.Equal(At(0, 9, 500), doc.Lines[0].Start);
        Assert.NotEqual(At(0, 10), doc.Lines[0].Start);
    }

    [Fact]
    public void Handles_crlf_line_endings()
    {
        var doc = Parse("[00:10.00]One\r\n[00:20.00]Two\r\n");

        Assert.Equal(["One", "Two"], doc.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Preserves_cjk_mixed_scripts_and_emoji()
    {
        var doc = Parse("""
            [00:10.00]君の名は
            [00:12.00]안녕하세요 world
            [00:14.00]Пример текста
            [00:16.00]Feeling 🎵 good
            """);

        Assert.Equal(
            ["君の名は", "안녕하세요 world", "Пример текста", "Feeling 🎵 good"],
            doc.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Preserves_cjk_in_word_stamps()
    {
        var doc = Parse("[00:10.00]<00:10.00>君の<00:11.00>名は");

        var words = doc.Lines[0].Words!;
        Assert.Equal(["君の", "名は"], words.Select(w => w.Text));
        Assert.Equal("君の名は", doc.Lines[0].Text);
    }
}
