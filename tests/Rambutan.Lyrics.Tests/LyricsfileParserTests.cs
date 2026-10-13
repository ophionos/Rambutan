using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Parsing;

namespace Rambutan.Lyrics.Tests;

/// <summary>Lyricsfile 1.0 (LRCLIB's word-level format) into the lyrics model,
/// failing closed on anything the YAML subset doesn't cover.</summary>
public class LyricsfileParserTests
{
    private static TimeSpan Ms(long ms) => TimeSpan.FromMilliseconds(ms);

    private static LyricsDocument Parse(string yaml) =>
        LyricsfileParser.Parse(yaml, "lrclib") ?? throw new Xunit.Sdk.XunitException("Parse returned null.");

    // The shape LRCLIB writes for a word-synced record: single-quoted text.
    private const string SingleQuoted = """
        version: '1.0'

        metadata:
          title: 'Paper Lanterns'
          artist: 'The Quiet Harbour'

        lines:
          - text: 'Paper lanterns'
            start_ms: 17120
            end_ms: 21800
            words:
              - text: 'Paper '
                start_ms: 17120
                end_ms: 17600
              - text: 'lanterns'
                start_ms: 17600
                end_ms: 19000
          - text: 'Don''t go'
            start_ms: 21800
            end_ms: 24000
            words:
              - text: 'Don''t '
                start_ms: 21800
              - text: 'go'
                start_ms: 22300

        plain: |
          Paper lanterns
          Don't go
        """;

    [Fact]
    public void Word_synced_lines_carry_their_words()
    {
        var doc = Parse(SingleQuoted);

        Assert.True(doc.IsSynced);
        Assert.Equal("lrclib", doc.Source);
        Assert.Equal(["Paper lanterns", "Don't go"], doc.Lines.Select(l => l.Text));
        var first = doc.Lines[0];
        Assert.Equal(Ms(17120), first.Start);
        Assert.Equal(Ms(21800), first.End);
        Assert.Equal(["Paper ", "lanterns"], first.Words!.Select(w => w.Text));
        Assert.Equal(Ms(17600), first.Words![1].Start);
        Assert.Equal(Ms(19000), first.Words![1].End);
    }

    [Fact]
    public void A_missing_word_end_is_the_next_words_start_and_the_last_closes_with_its_line()
    {
        var second = Parse(SingleQuoted).Lines[1];

        Assert.Equal(Ms(22300), second.Words![0].End);
        // The last line closes at the file's end_ms, which LRC can't say.
        Assert.Equal(Ms(24000), second.End);
        Assert.Equal(Ms(24000), second.Words![1].End);
    }

    [Fact]
    public void Plain_and_double_quoted_scalars_read_like_single_quoted_ones()
    {
        // The shape LRCLIB writes for a line-level record (plain scalars, the
        // sequence at its key's indentation), with double-quoted words mixed in.
        var doc = Parse("""
            version: "1.0"
            metadata:
              title: Paper Lanterns
              artist: The Quiet Harbour
            lines:
            - text: I said "go", then 'stay'
              start_ms: 1000
              words:
              - text: "I "
                start_ms: 1000
              - text: "said \"go\", "
                start_ms: 1200
              - text: then 'stay'
                start_ms: 1600
                end_ms: 2000
            """);

        var line = Assert.Single(doc.Lines);
        Assert.Equal("I said \"go\", then 'stay'", line.Text);
        Assert.Equal(["I ", "said \"go\", ", "then 'stay'"], line.Words!.Select(w => w.Text));
        Assert.Null(line.End);
    }

    [Fact]
    public void Keys_may_come_in_any_order_and_comments_are_ignored()
    {
        var doc = Parse("""
            # A comment before the document
            ---
            lines:
              - start_ms: 500 # starts here
                words:
                  - start_ms: 500
                    text: 'Hi'
                text: Hi
            version: '1.0'
            metadata: {}
            """.Replace("metadata: {}", "metadata:\n  title: T\n  artist: A"));

        var line = Assert.Single(doc.Lines);
        Assert.Equal(Ms(500), line.Start);
        Assert.Equal("Hi", Assert.Single(line.Words!).Text);
    }

    [Fact]
    public void Double_quoted_escapes_decode()
    {
        var doc = Parse("""
            version: '1.0'
            metadata:
              title: T
              artist: A
            lines:
              - text: "café \"\\\" \U0001F319"
                start_ms: 0
            """);

        Assert.Equal("café \"\\\" 🌙", doc.Lines[0].Text);
    }

    [Fact]
    public void Lines_are_sorted_by_start_and_each_ends_where_the_next_begins()
    {
        var doc = Parse("""
            version: '1.0'
            metadata:
              title: T
              artist: A
            lines:
              - text: second
                start_ms: 3000
                end_ms: 9000
              - text: first
                start_ms: 1000
                end_ms: 2000
            """);

        Assert.Equal(["first", "second"], doc.Lines.Select(l => l.Text));
        // The file's end leaves a gap; the model's line ends where the next starts.
        Assert.Equal(Ms(3000), doc.Lines[0].End);
        Assert.Equal(Ms(9000), doc.Lines[1].End);
    }

    [Fact]
    public void Words_that_dont_spell_the_line_are_dropped_and_the_line_kept()
    {
        var doc = Parse("""
            version: '1.0'
            metadata:
              title: T
              artist: A
            lines:
              - text: 'Paper lanterns'
                start_ms: 0
                words:
                  - text: 'Paper '
                    start_ms: 0
                  - text: 'boats'
                    start_ms: 400
            """);

        var line = Assert.Single(doc.Lines);
        Assert.Equal("Paper lanterns", line.Text);
        Assert.Null(line.Words);
    }

    [Fact]
    public void A_word_ending_before_it_starts_is_left_open()
    {
        var doc = Parse("""
            version: '1.0'
            metadata:
              title: T
              artist: A
            lines:
              - text: 'ab'
                start_ms: 1000
                words:
                  - text: 'a'
                    start_ms: 1000
                    end_ms: 900
                  - text: 'b'
                    start_ms: 1100
            """);

        Assert.Null(doc.Lines[0].Words![0].End);
    }

    [Theory]
    [InlineData("version: '2.0'\nmetadata:\n  title: T\n  artist: A\nlines:\n  - text: a\n    start_ms: 0")]
    [InlineData("version: 1.0.1\nmetadata:\n  title: T\n  artist: A\nlines:\n  - text: a\n    start_ms: 0")]
    [InlineData("metadata:\n  title: T\n  artist: A\nlines:\n  - text: a\n    start_ms: 0")]
    public void An_unknown_or_missing_version_is_not_read(string yaml) =>
        Assert.Null(LyricsfileParser.Parse(yaml, "lrclib"));

    [Theory]
    // Flow collections, anchors and aliases, tags.
    [InlineData("version: '1.0'\nlines: [{text: a, start_ms: 0}]")]
    [InlineData("version: '1.0'\nlines:\n  - &x\n    text: a\n    start_ms: 0\n  - *x")]
    [InlineData("version: !!str '1.0'\nlines:\n  - text: a\n    start_ms: 0")]
    // A quoted scalar running onto the next line, and a plain one continued.
    [InlineData("version: '1.0'\nlines:\n  - text: 'a\n      b'\n    start_ms: 0")]
    [InlineData("version: '1.0'\nlines:\n  - text: a\n      b\n    start_ms: 0")]
    // Duplicate keys, which the spec forbids, and a tab in the indentation.
    [InlineData("version: '1.0'\nlines:\n  - text: a\n    text: b\n    start_ms: 0")]
    [InlineData("version: '1.0'\nlines:\n\t- text: a\n\t  start_ms: 0")]
    // Not YAML at all: an LRC file.
    [InlineData("[00:01.00]Paper lanterns")]
    public void Yaml_outside_the_subset_is_not_read(string yaml) =>
        Assert.Null(LyricsfileParser.Parse(yaml, "lrclib"));

    [Theory]
    [InlineData("  - text: a")]                       // No start.
    [InlineData("  - start_ms: 0")]                   // No text.
    [InlineData("  - text: a\n    start_ms: -5")]     // A negative start.
    [InlineData("  - text: a\n    start_ms: 1.5")]    // Not whole milliseconds.
    public void A_line_without_a_usable_text_and_start_fails_the_file(string line) =>
        Assert.Null(LyricsfileParser.Parse($"version: '1.0'\nmetadata:\n  title: T\n  artist: A\nlines:\n{line}", "lrclib"));

    [Fact]
    public void No_synced_lines_is_no_document() =>
        Assert.Null(LyricsfileParser.Parse("version: '1.0'\nmetadata:\n  title: T\n  artist: A\nplain: |\n  only plain\n", "lrclib"));

    [Fact]
    public void Block_scalars_are_read_through_to_the_next_key()
    {
        // A plain block before the lines, with a blank line and a line that
        // looks like a key, must not end the block early.
        var doc = Parse("""
            version: '1.0'
            metadata:
              title: T
              artist: A
            plain: |-
              first: not a key

              - not an item
            lines:
              - text: a
                start_ms: 0
            """);

        Assert.Equal("a", Assert.Single(doc.Lines).Text);
    }

    [Fact]
    public void The_recorded_shape_reads_end_to_end()
    {
        var json = System.Text.Json.JsonDocument.Parse(Fixtures.Read("lrclib-get.word-synced.synthetic.json"));
        var yaml = json.RootElement.GetProperty("lyricsfile").GetString()!;

        var doc = Parse(yaml);

        Assert.Equal(4, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.NotNull(l.Words));
        Assert.Equal(["夜", "の", "灯", "り"], doc.Lines[2].Words!.Select(w => w.Text));
        Assert.Equal("Don't let the light go out", doc.Lines[1].Text);
    }
}
