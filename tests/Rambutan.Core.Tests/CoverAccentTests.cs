using Rambutan.Core.Appearance;
using Rambutan.Core.Visualizer;

namespace Rambutan.Core.Tests;

/// <summary>The accent taken from a cover: no colour gives the default red, a
/// weak colour is raised to a clear one of the same hue, a vivid one is kept,
/// and similar covers give similar accents.</summary>
public class CoverAccentTests
{
    private static Rgb Hex(string hex) => new(
        Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));

    /// <summary>What the app shows for a cover of one solid colour: the
    /// visualizer's pick, then the cover rule, then the palette.</summary>
    private static Rgb Shown(string coverHex)
    {
        var cover = Hex(coverHex);
        var rgba = new byte[16 * 4];
        for (var i = 0; i < 16; i++)
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (cover.R, cover.G, cover.B, 255);
        var (r, g, b) = ArtworkColor.Pick(rgba)!.Value;
        return AccentPalette.From(AccentPalette.ForCover(new Rgb(r, g, b))).Base;
    }

    private static double HueDistance(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    [Theory]
    [InlineData("#808080")]
    [InlineData("#D6D6D6")]
    [InlineData("#000000")]
    [InlineData("#C8C6C2")] // a warm near-grey
    public void NoUsableColour_GivesTheDefaultAccent(string hex)
    {
        Assert.Equal(AccentPalette.Default.Base, AccentPalette.ForCover(Hex(hex)));
    }

    [Fact]
    public void TheVisualizersNeutral_GivesTheDefaultAccent()
    {
        var (r, g, b) = ArtworkColor.Neutral;
        Assert.Equal(AccentPalette.Default.Base, AccentPalette.ForCover(new Rgb(r, g, b)));
    }

    [Theory]
    [InlineData("#D8CFA0")] // pastel yellow
    [InlineData("#A0B8D8")] // pastel blue
    [InlineData("#C8D8B0")] // pastel green
    [InlineData("#D8B0C0")] // pastel pink
    [InlineData("#B8A8D0")] // lavender
    [InlineData("#A8D0D0")] // pastel teal
    [InlineData("#C79A6D")] // tan: a dark brown cover's pick
    public void AWeakColour_IsRaisedToTheFloor_KeepingItsHue(string hex)
    {
        var cover = Hex(hex);
        var raw = AccentPalette.ForCover(cover);
        var shown = AccentPalette.From(raw).Base;

        var before = Oklch.Of(cover);
        var after = Oklch.Of(shown);
        Assert.True(after.C >= AccentPalette.CoverMinChroma - 0.006, $"{hex} gives {shown}, chroma {after.C:F3}");
        Assert.True(HueDistance(before.H, Oklch.Of(raw).H) <= 3, $"{hex} gives {raw}, hue {before.H:F1} → {Oklch.Of(raw).H:F1}");
        Assert.True(HueDistance(before.H, after.H) <= 6, $"{hex} shows {shown}, hue {before.H:F1} → {after.H:F1}");
    }

    [Theory]
    [InlineData("#D8CFA0")] // pastel yellow
    [InlineData("#A0B8D8")] // pastel blue
    [InlineData("#5C4033")] // dark brown
    [InlineData("#C0A8D8")] // lavender
    public void ThroughTheVisualizersPick_AWeakCoverShowsAClearAccent(string coverHex)
    {
        var shown = Shown(coverHex);

        Assert.NotEqual(AccentPalette.Default.Base, shown);
        Assert.True(Oklch.Of(shown).C >= AccentPalette.CoverMinChroma - 0.006, $"{coverHex} shows {shown}");
    }

    [Theory]
    [InlineData("#FF4E45")]
    [InlineData("#E03030")]
    [InlineData("#6BCB77")]
    [InlineData("#5B8DEF")]
    [InlineData("#FFD93D")]
    [InlineData("#1A237E")] // navy: dark, but vivid
    public void AVividColour_IsUnchanged(string hex)
    {
        Assert.Equal(hex, AccentPalette.ForCover(Hex(hex)).ToString());
    }

    [Fact]
    public void AColourJustUnderTheFloor_MovesTooLittleToSee()
    {
        // A light cyan, chroma 0.127.
        var cyan = Hex("#4FC3F7");
        Assert.True(Oklch.Difference(cyan, AccentPalette.ForCover(cyan)) < 0.01);
    }

    [Theory]
    [InlineData("#D8CFA0")]
    [InlineData("#A0B8D8")]
    [InlineData("#5C4033")]
    [InlineData("#C8D8B0")]
    [InlineData("#A8D0D0")]
    [InlineData("#E03030")]
    [InlineData("#808080")]
    public void EveryCoverAccent_KeepsThePalettesReadability(string hex)
    {
        var palette = AccentPalette.From(AccentPalette.ForCover(Hex(hex)));

        Assert.True(AccentPalette.ContrastRatio(palette.Base, AccentPalette.Panel) >= AccentPalette.MinTextContrast);
        var onAccent = AccentPalette.ContrastRatio(palette.OnAccent, palette.Base);
        Assert.True(onAccent >= (palette.OnAccent == AccentPalette.White ? AccentPalette.MinWhiteOnAccent : 4.5));
    }

    [Theory]
    [InlineData(0.80, 98)]   // yellow
    [InlineData(0.72, 256)]  // blue
    [InlineData(0.75, 46)]   // orange-brown
    [InlineData(0.78, 210)]  // teal, where sRGB has least chroma to give
    public void AsChromaRises_TheAccentNeverLosesChroma_AndNeverJumps(double l, double h)
    {
        Rgb? last = null;
        var lastChroma = 0.0;
        for (var c = AccentPalette.CoverNoColorChroma + 0.002; c < 0.2; c += 0.002)
        {
            if (!Oklch.InGamut(l, c, h))
                break;
            var shown = AccentPalette.ForCover(Oklch.ToRgb(l, c, h));
            var chroma = Oklch.Of(shown).C;
            Assert.True(chroma >= lastChroma - 0.004, $"chroma {c:F3}: {chroma:F3} after {lastChroma:F3}");
            if (last is { } previous)
                Assert.True(Oklch.Difference(previous, shown) < 0.02, $"chroma {c:F3}: {previous} → {shown}");
            last = shown;
            lastChroma = chroma;
        }
    }

    [Theory]
    [InlineData(0.08)]
    [InlineData(0.05)]
    public void AcrossHues_AWeakColoursAccentNeverJumps(double c)
    {
        foreach (var l in new[] { 0.45, 0.6, 0.75, 0.88 })
        {
            Rgb? last = null;
            for (var h = 0.0; h <= 360; h += 1)
            {
                if (!Oklch.InGamut(l, c, h))
                {
                    last = null;
                    continue;
                }
                var shown = AccentPalette.ForCover(Oklch.ToRgb(l, c, h));
                Assert.True(Oklch.Of(shown).C >= AccentPalette.CoverMinChroma - 0.006, $"L {l} h {h}: {shown}");
                if (last is { } previous)
                    Assert.True(Oklch.Difference(previous, shown) < 0.03, $"L {l} h {h}: {previous} → {shown}");
                last = shown;
            }
        }
    }

    [Theory]
    [InlineData(46)]
    [InlineData(210)]
    [InlineData(256)]
    public void AcrossLightness_AWeakColoursAccentNeverJumps(double h)
    {
        Rgb? last = null;
        for (var l = 0.3; l <= 0.95; l += 0.005)
        {
            if (!Oklch.InGamut(l, 0.06, h))
            {
                last = null;
                continue;
            }
            var shown = AccentPalette.ForCover(Oklch.ToRgb(l, 0.06, h));
            // The input is rounded to 8 bits, which moves a dark colour's hue
            // by a degree or two between steps, and the edge of sRGB with it.
            if (last is { } previous)
                Assert.True(Oklch.Difference(previous, shown) < 0.03, $"L {l:F3}: {previous} → {shown}");
            last = shown;
        }
    }

    [Fact]
    public void Oklch_MatchesKnownValues()
    {
        var red = Oklch.Of(Hex("#FF0000"));
        Assert.Equal(0.628, red.L, 3);
        Assert.Equal(0.258, red.C, 3);
        Assert.Equal(29.2, red.H, 1);
        Assert.Equal(0, Oklch.Of(Hex("#808080")).C, 3);
        Assert.Equal(Hex("#5B8DEF"), Oklch.ToRgb(Oklch.Of(Hex("#5B8DEF")).L, Oklch.Of(Hex("#5B8DEF")).C, Oklch.Of(Hex("#5B8DEF")).H));
    }
}
