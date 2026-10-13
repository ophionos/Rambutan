using Rambutan.Lyrics.Models;

namespace Rambutan.Lyrics.Tests;

/// <summary>How a position reads against line and word timings.</summary>
public class LyricTimingTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Theory]
    [InlineData(0, -1)]      // before the first line
    [InlineData(3.999, -1)]
    [InlineData(4, 0)]       // exactly on a stamp: that line
    [InlineData(7.5, 0)]
    [InlineData(8, 1)]
    [InlineData(12, 2)]
    [InlineData(19.999, 3)]
    [InlineData(20, 4)]      // the last line holds to the end
    [InlineData(3600, 4)]
    public void FindActiveLine_IsTheLastLineStartedByNow(double seconds, int expected)
    {
        var starts = new[] { 4, 8, 12, 16, 20 }.Select(s => TimeSpan.FromSeconds(s)).ToArray();
        Assert.Equal(expected, LyricTiming.FindActiveLine(starts, TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void FindActiveLine_NoLines_IsNone() =>
        Assert.Equal(-1, LyricTiming.FindActiveLine([], TimeSpan.FromSeconds(10)));

    [Fact]
    public void FindActiveLine_RepeatedStamps_TakesTheLastOfThem()
    {
        TimeSpan[] starts = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9)];
        Assert.Equal(2, LyricTiming.FindActiveLine(starts, TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(0.999, 0.0)]   // before the start
    [InlineData(1.0, 0.0)]     // reached, nothing sung yet
    [InlineData(1.5, 0.5)]
    [InlineData(1.999, 0.999)]
    [InlineData(2.0, 1.0)]     // at the end
    [InlineData(60, 1.0)]
    public void Progress_IsLinearAcrossTheWord(double seconds, double expected) =>
        Assert.Equal(expected, LyricTiming.Progress(new LyricWord("word ", S(1), S(2)), S(seconds)), 6);

    [Fact]
    public void Progress_ZeroLengthWord_IsCompleteOnceReached()
    {
        var word = new LyricWord("blink ", S(3), S(3));

        Assert.Equal(0, LyricTiming.Progress(word, S(2.999)));
        Assert.Equal(1, LyricTiming.Progress(word, S(3)));
    }

    [Fact]
    public void Progress_OpenWord_IsCompleteOnceReached()
    {
        var word = new LyricWord("end", S(3));

        Assert.Equal(0, LyricTiming.Progress(word, S(2.999)));
        Assert.Equal(1, LyricTiming.Progress(word, S(3)));
    }

    [Fact]
    public void Progress_EqualWordStampsFromTheParser_NeverDivideByZero()
    {
        var doc = Parsing.LrcParser.Parse("[00:01.00]<00:01.00>a <00:01.00>b <00:02.00>c\n[00:03.00]next", "test");
        var words = doc.Lines[0].Words!;

        Assert.All(words, w => Assert.InRange(LyricTiming.Progress(w, S(1)), 0, 1));
        Assert.Equal(1, LyricTiming.Progress(words[0], S(1)));
        Assert.Equal(0, LyricTiming.Progress(words[1], S(1)), 6);
    }

    [Fact]
    public void FindActiveLine_LinesCollapsedOntoZero_ActivateTheLastOfThem()
    {
        // A large [offset:] clamps the opening lines to 0:00. The rule: the later
        // line wins, the earlier ones are never active.
        var doc = Parsing.LrcParser.Parse("[offset:+5000]\n[00:01.00]one\n[00:02.00]two\n[00:09.00]three", "test");
        var starts = doc.Lines.Select(l => l.Start).ToArray();

        Assert.Equal(1, LyricTiming.FindActiveLine(starts, TimeSpan.Zero));
        Assert.Equal(2, LyricTiming.FindActiveLine(starts, S(4)));
    }
}
