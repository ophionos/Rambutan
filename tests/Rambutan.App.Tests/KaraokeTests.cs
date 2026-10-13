using Avalonia;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Parsing;

namespace Rambutan.App.Tests;

/// <summary>The karaoke sweep's pure parts: which letters a word covers,
/// and which part of its text rectangles is sung.</summary>
public class KaraokeTests
{
    private static LyricLine Line(string lrc) => LrcParser.Parse(lrc, "test").Lines[0];

    [Fact]
    public void Spans_CoverEachWordOfTheDisplayedText()
    {
        var line = Line("[00:01.00]<00:01.00>Say <00:01.50>it <00:02.00>once");

        var spans = KaraokeSpan.Build(line.Text, line.Words)!;

        Assert.Equal("Say it once", line.Text);
        Assert.Equal(["Say ", "it ", "once"], spans.Select(s => line.Text.Substring(s.Start, s.Length)));
        Assert.Equal(TimeSpan.FromSeconds(1.5), spans[1].Word.Start);
    }

    [Fact]
    public void Spans_SkipTheLeadingSpaceTheTextTrims()
    {
        var line = Line("[00:01.00] <00:01.00>夜<00:01.20>の<00:01.40>街");

        var spans = KaraokeSpan.Build(line.Text, line.Words)!;

        Assert.Equal("夜の街", line.Text);
        Assert.Equal(["夜", "の", "街"], spans.Select(s => line.Text.Substring(s.Start, s.Length)));
    }

    [Fact]
    public void Spans_AreNull_WithoutWordTimings_OrWhenTheyDontSpellTheText()
    {
        Assert.Null(KaraokeSpan.Build("plain line", null));

        var line = Line("[00:01.00]<00:01.00>Say <00:01.50>it");
        Assert.Null(KaraokeSpan.Build("Something else", line.Words));
    }

    [Fact]
    public void ALineFromTheViewModel_CarriesItsSpans_AndAGapNone()
    {
        var timed = new LyricLineViewModel(Line("[00:01.00]<00:01.00>Say <00:01.50>it"), 0);
        var gap = new LyricLineViewModel(new LyricLine("", TimeSpan.FromSeconds(3)), 1);

        Assert.Equal(2, timed.Words!.Count);
        Assert.Null(gap.Words);
    }

    private static readonly Rect[] TwoLines = [new(0, 0, 60, 20), new(0, 20, 40, 20)];

    [Fact]
    public void Sung_NothingBeforeTheWord_EverythingAfterIt()
    {
        Assert.Empty(KaraokeGeometry.Sung(TwoLines, 0, 10).Solid);
        var done = KaraokeGeometry.Sung(TwoLines, 1, 10);
        Assert.Equal(TwoLines, done.Solid);
        Assert.Null(done.Edge);
    }

    [Fact]
    public void Sung_FillsTheFirstVisualLineBeforeTheNext()
    {
        // 100 px in all; 70 % is the whole first rect plus 10 px of the second.
        var sung = KaraokeGeometry.Sung(TwoLines, 0.7, 0);

        Assert.Equal([new Rect(0, 0, 60, 20), new Rect(0, 20, 10, 20)], sung.Solid);
        Assert.Null(sung.Edge);
    }

    [Fact]
    public void Sung_TheSoftEdgeIsCentredOnTheSweep()
    {
        var sung = KaraokeGeometry.Sung([new Rect(10, 0, 100, 20)], 0.5, 10);

        Assert.Equal([new Rect(10, 0, 45, 20)], sung.Solid);
        Assert.Equal(new Rect(55, 0, 10, 20), sung.Edge);
    }

    [Fact]
    public void Sung_TheSoftEdgeStaysInsideItsRectangle()
    {
        var start = KaraokeGeometry.Sung([new Rect(0, 0, 100, 20)], 0.02, 10);
        Assert.Empty(start.Solid);
        Assert.Equal(new Rect(0, 0, 7, 20), start.Edge);

        var end = KaraokeGeometry.Sung([new Rect(0, 0, 100, 20)], 0.98, 10);
        Assert.Equal(new Rect(93, 0, 7, 20), end.Edge);
    }
}
