using Rambutan.Core.Appearance;

namespace Rambutan.Core.Tests;

/// <summary>The colours derived from the accent: the default kept exactly,
/// text on the accent readable, and the accent readable as text.</summary>
public class AccentPaletteTests
{
    private static Rgb Hex(string hex) => new(
        Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));

    [Fact]
    public void TheDefaultRed_GivesTheHandTunedPalette()
    {
        var palette = AccentPalette.From(Hex("#FF4E45"));

        Assert.Same(AccentPalette.Default, palette);
        Assert.Equal("#FF4E45", palette.Base.ToString());
        Assert.Equal("#FF8D86", palette.Light2.ToString());
        Assert.Equal("#FF6157", palette.Hover.ToString());
        Assert.Equal("#E5423A", palette.Pressed.ToString());
        Assert.Equal(AccentPalette.White, palette.OnAccent);
    }

    [Fact]
    public void TheDefaultRed_KeepsWhiteText_ThoughItIsBelow4Point5()
    {
        var contrast = AccentPalette.ContrastRatio(AccentPalette.White, AccentPalette.Default.Base);

        Assert.InRange(contrast, AccentPalette.MinWhiteOnAccent, 4.5);
    }

    [Theory]
    [InlineData("#FFD93D")] // yellow
    [InlineData("#6BCB77")] // green
    [InlineData("#4FC3F7")] // cyan
    [InlineData("#B388FF")] // violet
    [InlineData("#FFFFFF")]
    public void ALightAccent_GetsDarkText(string hex)
    {
        var palette = AccentPalette.From(Hex(hex));

        Assert.Equal(AccentPalette.Ground, palette.OnAccent);
        Assert.True(AccentPalette.ContrastRatio(palette.OnAccent, palette.Base) >= 4.5);
    }

    [Theory]
    [InlineData("#5B8DEF")] // blue
    [InlineData("#F06292")] // pink
    [InlineData("#C62828")]
    public void ADarkerAccent_KeepsWhiteText(string hex)
    {
        var palette = AccentPalette.From(Hex(hex));

        Assert.Equal(AccentPalette.White, palette.OnAccent);
        Assert.True(AccentPalette.ContrastRatio(palette.OnAccent, palette.Base) >= AccentPalette.MinWhiteOnAccent);
    }

    [Theory]
    [InlineData("#5B8DEF")]
    [InlineData("#FFD93D")]
    [InlineData("#4FC3F7")]
    public void AReadableAccent_IsUsedAsChosen(string hex)
    {
        Assert.Equal(hex, AccentPalette.From(Hex(hex)).Base.ToString());
    }

    [Theory]
    [InlineData("#1A237E")] // navy
    [InlineData("#000000")]
    [InlineData("#3E2723")]
    public void ADarkAccent_IsLightened_JustEnoughToReadOnAPanel(string hex)
    {
        var chosen = Hex(hex);
        var shown = AccentPalette.From(chosen).Base;

        var contrast = AccentPalette.ContrastRatio(shown, AccentPalette.Panel);
        Assert.InRange(contrast, AccentPalette.MinTextContrast, AccentPalette.MinTextContrast + 0.1);
        // Lightened towards white, so the hue stays: no channel goes down.
        Assert.True(shown.R >= chosen.R && shown.G >= chosen.G && shown.B >= chosen.B);
    }

    [Fact]
    public void TheRamp_RunsFromLightToDark()
    {
        var p = AccentPalette.From(Hex("#5B8DEF"));
        double[] order =
        [
            AccentPalette.Luminance(p.Light3), AccentPalette.Luminance(p.Light2), AccentPalette.Luminance(p.Light1),
            AccentPalette.Luminance(p.Base),
            AccentPalette.Luminance(p.Dark1), AccentPalette.Luminance(p.Dark2), AccentPalette.Luminance(p.Dark3),
        ];

        for (var i = 1; i < order.Length; i++)
            Assert.True(order[i] < order[i - 1], $"step {i} isn't darker than step {i - 1}");
        Assert.True(AccentPalette.Luminance(p.Hover) > AccentPalette.Luminance(p.Base));
        Assert.True(AccentPalette.Luminance(p.Pressed) < AccentPalette.Luminance(p.Base));
    }

    [Fact]
    public void TheDerivedRamp_IsCloseToTheHandTunedOne()
    {
        // The default is pinned, but the derivation uses its spacing: a red one
        // step off the default lands within a few levels of each pinned value.
        var derived = AccentPalette.From(Hex("#FF4E46"));
        var pinned = AccentPalette.Default;

        foreach (var (a, b) in new[]
        {
            (derived.Light1, pinned.Light1), (derived.Light2, pinned.Light2), (derived.Light3, pinned.Light3),
            (derived.Dark1, pinned.Dark1), (derived.Dark2, pinned.Dark2), (derived.Dark3, pinned.Dark3),
            (derived.Hover, pinned.Hover), (derived.Pressed, pinned.Pressed),
        })
        {
            Assert.InRange(Math.Abs(a.R - b.R), 0, 6);
            Assert.InRange(Math.Abs(a.G - b.G), 0, 6);
            Assert.InRange(Math.Abs(a.B - b.B), 0, 6);
        }
    }

    [Fact]
    public void ContrastRatio_MatchesWcag()
    {
        Assert.Equal(21, AccentPalette.ContrastRatio(new(0, 0, 0), AccentPalette.White), 2);
        Assert.Equal(1, AccentPalette.ContrastRatio(AccentPalette.Panel, AccentPalette.Panel), 6);
    }
}
