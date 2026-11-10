using Rambutan.Core.Text;

namespace Rambutan.Core.Tests;

public class SettingsFilterTests
{
    private static readonly string[] Crossfade = ["Playback", "Crossfade", "Fades one track into the next."];

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_query_matches_everything(string? query)
    {
        Assert.True(SettingsFilter.IsEmpty(query));
        Assert.True(SettingsFilter.Matches(query, Crossfade));
        Assert.True(SettingsFilter.Matches(query, []));
    }

    [Fact]
    public void A_word_in_the_title_matches() =>
        Assert.True(SettingsFilter.Matches("crossfade", Crossfade));

    [Fact]
    public void A_word_in_the_help_matches() =>
        Assert.True(SettingsFilter.Matches("track", Crossfade));

    [Fact]
    public void Part_of_a_word_matches() =>
        Assert.True(SettingsFilter.Matches("fade", Crossfade));

    [Fact]
    public void Every_word_must_occur_somewhere_in_title_or_help()
    {
        Assert.True(SettingsFilter.Matches("crossfade next", Crossfade));
        Assert.False(SettingsFilter.Matches("crossfade lyrics", Crossfade));
    }

    [Fact]
    public void A_query_can_split_across_the_heading_and_the_row() =>
        Assert.True(SettingsFilter.Matches("playback  crossfade", Crossfade));

    [Fact]
    public void Case_does_not_matter() =>
        Assert.True(SettingsFilter.Matches("CROSSFADE Next", Crossfade));

    [Fact]
    public void Diacritics_do_not_matter_either_way()
    {
        Assert.True(SettingsFilter.Matches("cafe", ["Café"]));
        Assert.True(SettingsFilter.Matches("Écran", ["ecran"]));
    }

    [Fact]
    public void A_word_that_occurs_nowhere_does_not_match()
    {
        Assert.False(SettingsFilter.Matches("equalizer", Crossfade));
        Assert.False(SettingsFilter.Matches("crossfade", [null, ""]));
    }

    [Fact]
    public void Words_split_on_any_whitespace() =>
        Assert.Equal(["a", "b", "c"], SettingsFilter.Words(" a\tb\n c "));
}
