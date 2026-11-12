using Rambutan.Core.Appearance;

namespace Rambutan.Core.Tests;

/// <summary>Lyrics keep 4.5:1 against the brightest bar of the dimmed stage.</summary>
public class LyricsLegibilityTests
{
    public static TheoryData<string, byte, byte, byte> Bars => new()
    {
        { "the default accent", AccentPalette.Default.Base.R, AccentPalette.Default.Base.G, AccentPalette.Default.Base.B },
        { "white", 0xFF, 0xFF, 0xFF },
    };

    [Theory]
    [MemberData(nameof(Bars))]
    public void TheInactiveLine_ReachesTheMinimumContrast(string name, byte r, byte g, byte b)
    {
        var contrast = LyricsLegibility.TextContrast(new Rgb(r, g, b), LyricsLegibility.InactiveLineOpacity);
        Assert.True(contrast >= LyricsLegibility.MinContrast, $"{name}: {contrast:F2}:1");
    }

    [Theory]
    [MemberData(nameof(Bars))]
    public void TheFooterText_ReachesTheMinimumContrast(string name, byte r, byte g, byte b)
    {
        var contrast = LyricsLegibility.TextContrast(new Rgb(r, g, b), LyricsLegibility.FooterTextOpacity);
        Assert.True(contrast >= LyricsLegibility.MinContrast, $"{name}: {contrast:F2}:1");
    }

    [Fact]
    public void TheLines_StayOrderedByDistanceFromThePlayingOne() =>
        Assert.True(LyricsLegibility.InactiveLineOpacity < LyricsLegibility.NearLineOpacity
                    && LyricsLegibility.NearLineOpacity < 1);

    [Fact]
    public void TheStageDim_LeavesTheVisualizerVisible() =>
        Assert.InRange(LyricsLegibility.StageDim, 0.12, 0.3);

    [Fact]
    public void TheOldInactiveOpacity_DidNotReachIt() =>
        Assert.True(LyricsLegibility.TextContrast(new Rgb(255, 255, 255), 0.34) < LyricsLegibility.MinContrast);
}
