using Rambutan.Lyrics.Models;

namespace Rambutan.Lyrics.Tests;

public class LyricsDocumentTests
{
    [Fact]
    public void FromPlainText_keeps_lines_unsynced()
    {
        var doc = LyricsDocument.FromPlainText("First\nSecond\nThird", "test");

        Assert.False(doc.IsSynced);
        Assert.Equal(["First", "Second", "Third"], doc.Lines.Select(l => l.Text));
        Assert.All(doc.Lines, l => Assert.Equal(TimeSpan.Zero, l.Start));
        Assert.All(doc.Lines, l => Assert.Null(l.Words));
    }

    [Fact]
    public void FromPlainText_handles_crlf_and_trims_trailing_padding()
    {
        var doc = LyricsDocument.FromPlainText("First\r\nSecond\r\n\r\n\r\n", "test");

        Assert.Equal(["First", "Second"], doc.Lines.Select(l => l.Text));
    }

    [Fact]
    public void FromPlainText_keeps_interior_blank_lines()
    {
        var doc = LyricsDocument.FromPlainText("Verse\n\nChorus", "test");

        Assert.Equal(["Verse", "", "Chorus"], doc.Lines.Select(l => l.Text));
    }

    [Fact]
    public void FromPlainText_strips_a_utf8_bom()
    {
        // Same defect LrcParser.Parse was hardened against: a BOM is not
        // whitespace, so it survives trimming and renders at the head of line one.
        var doc = LyricsDocument.FromPlainText("\uFEFFFirst\nSecond", "test");

        Assert.Equal(["First", "Second"], doc.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Empty_document_is_unsynced_and_has_no_lines()
    {
        var doc = LyricsDocument.Empty("test");

        Assert.False(doc.IsSynced);
        Assert.Empty(doc.Lines);
        Assert.Equal(0, doc.OffsetMs);
    }

    [Fact]
    public void ClosedAt_ClosesTheLastLineAndItsLastWord()
    {
        var doc = Parsing.LrcParser.Parse("[00:01.00]first\n[00:05.00]<00:05.00>last <00:06.00>words", "test");

        var closed = doc.ClosedAt(TimeSpan.FromSeconds(9));

        Assert.Equal(TimeSpan.FromSeconds(9), closed.Lines[^1].End);
        Assert.Equal(TimeSpan.FromSeconds(9), closed.Lines[^1].Words![^1].End);
        Assert.Equal(TimeSpan.FromSeconds(6), closed.Lines[^1].Words![0].End);
        Assert.Equal(doc.Lines[0], closed.Lines[0]);
    }

    [Fact]
    public void ClosedAt_TrackEndingBeforeTheLastLine_ChangesNothing()
    {
        var doc = Parsing.LrcParser.Parse("[00:01.00]first\n[00:05.00]last", "test");

        Assert.Same(doc, doc.ClosedAt(TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void ClosedAt_LastWordStartingAfterTheEnd_StaysOpen()
    {
        var doc = Parsing.LrcParser.Parse("[00:05.00]<00:05.00>last <00:08.00>word", "test");

        var closed = doc.ClosedAt(TimeSpan.FromSeconds(7));

        Assert.Equal(TimeSpan.FromSeconds(7), closed.Lines[0].End);
        Assert.Null(closed.Lines[0].Words![^1].End);
    }

    [Fact]
    public void ClosedAt_Unsynced_ChangesNothing()
    {
        var doc = LyricsDocument.FromPlainText("a\nb", "test");

        Assert.Same(doc, doc.ClosedAt(TimeSpan.FromSeconds(9)));
    }
}
