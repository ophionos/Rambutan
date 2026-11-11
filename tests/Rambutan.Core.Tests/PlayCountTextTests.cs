using Rambutan.Core.Text;

namespace Rambutan.Core.Tests;

public class PlayCountTextTests
{
    [Theory]
    [InlineData(1_200_000_000L, "1.2", CountScale.Billions)]
    [InlineData(1_990_000_000L, "1.9", CountScale.Billions)]   // cut, not rounded up
    [InlineData(831_000_000L, "831", CountScale.Millions)]
    [InlineData(14_600_000L, "14", CountScale.Millions)]
    [InlineData(5_100_000L, "5.1", CountScale.Millions)]
    [InlineData(1_000_000L, "1", CountScale.Millions)]
    [InlineData(368_000L, "368", CountScale.Thousands)]
    [InlineData(999L, "999", CountScale.Units)]
    [InlineData(0L, "0", CountScale.Units)]
    [InlineData(-5L, "0", CountScale.Units)]
    public void Shorten_CutsToTheUnit(long count, string value, CountScale scale)
    {
        var shortened = PlayCountText.Shorten(count);

        Assert.Equal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), shortened.Value);
        Assert.Equal(scale, shortened.Scale);
    }

    [Theory]
    [InlineData("1.2B plays")]
    [InlineData("849M plays")]
    [InlineData("5.1M views")]
    [InlineData("368K plays")]
    [InlineData("812 plays")]
    public void ACountReadFromItsText_ShortensToThatText(string text)
    {
        Assert.True(PlayCountText.TryParse(text, out var count));

        var shortened = PlayCountText.Shorten(count);
        var suffix = shortened.Scale switch
        {
            CountScale.Billions => "B",
            CountScale.Millions => "M",
            CountScale.Thousands => "K",
            _ => "",
        };
        Assert.StartsWith(
            shortened.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + suffix + " ", text);
    }

    [Theory]
    [InlineData("1.2B plays", 1_200_000_000L)]
    [InlineData("849M plays", 849_000_000L)]
    [InlineData("368K plays", 368_000L)]
    [InlineData("674K views", 674_000L)]
    [InlineData("1.5K views", 1_500L)]
    [InlineData("812 plays", 812L)]
    [InlineData("1 play", 1L)]
    [InlineData("1,234 views", 1_234L)]
    public void PlayCount_ReadsTheShortForm(string text, long expected)
    {
        Assert.True(PlayCountText.TryParse(text, out var count));
        Assert.Equal(expected, count);
    }

    [Theory]
    [InlineData("12 songs")]            // a count, but not of plays
    [InlineData("1.2M subscribers")]
    [InlineData("50 Cent")]
    [InlineData("2013")]
    [InlineData("plays")]
    [InlineData("5:21")]
    [InlineData("1.2X plays")]
    [InlineData("")]
    public void PlayCount_RefusesEverythingElse(string text)
    {
        Assert.False(PlayCountText.TryParse(text, out _));
    }
}
