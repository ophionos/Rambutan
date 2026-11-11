using Rambutan.Core.Appearance;

namespace Rambutan.Core.Tests;

/// <summary>The blurred cover behind the shell: a vivid cover shows as it
/// always has, and the weaker its colour the greyer and darker it is drawn, so
/// a pastel reads as neutral dark rather than brown.</summary>
public class CoverBackdropTests
{
    private const int Pixels = 32 * 32;

    // The scrim PageBackground draws over the art, on the window's ground.
    private const double ScrimAlpha = 0xA6 / 255.0;

    private static Rgb Hex(string hex) => new(
        Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));

    private static byte[] Fill(Rgb c, int count = Pixels, byte alpha = 255)
    {
        var rgba = new byte[count * 4];
        for (var i = 0; i < count; i++)
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (c.R, c.G, c.B, alpha);
        return rgba;
    }

    /// <summary>The colour a cover of one colour leaves on the window: the art,
    /// treated and at its opacity, over the ground, under the scrim.</summary>
    private static Rgb Tint(Rgb cover, BackdropTreatment treatment)
    {
        var pixel = Fill(cover, 1);
        CoverBackdrop.Desaturate(pixel, treatment.Saturation);
        var ground = AccentPalette.Ground;
        double Channel(byte art, byte under)
        {
            var withArt = under + (art - under) * treatment.ArtOpacity;
            return withArt + (under - withArt) * ScrimAlpha;
        }
        return new(
            (byte)Math.Round(Channel(pixel[0], ground.R)),
            (byte)Math.Round(Channel(pixel[1], ground.G)),
            (byte)Math.Round(Channel(pixel[2], ground.B)));
    }

    private static BackdropTreatment TreatmentOf(Rgb cover) =>
        CoverBackdrop.For(CoverBackdrop.Chroma(Fill(cover)));

    [Theory]
    [InlineData("#E03030")]
    [InlineData("#2060F0")]
    [InlineData("#30C040")]
    [InlineData("#701818")] // dark, but vivid
    public void AVividCover_IsDrawnAsItAlwaysWas(string hex)
    {
        Assert.Equal(new BackdropTreatment(1, CoverBackdrop.ArtOpacity), TreatmentOf(Hex(hex)));
        Assert.Equal(0.85, CoverBackdrop.ArtOpacity);
    }

    [Fact]
    public void ACoverWithAVividTenth_IsDrawnAsItAlwaysWas()
    {
        // Mostly grey, with a fifth in a vivid blue: the blur keeps a patch of it.
        var rgba = Fill(Hex("#808080"));
        Fill(Hex("#2060F0"), Pixels / 5).CopyTo(rgba, 0);

        Assert.Equal(1, CoverBackdrop.For(CoverBackdrop.Chroma(rgba)).Saturation);
    }

    [Theory]
    [InlineData("#808080")]
    [InlineData("#F0F0F0")]
    [InlineData("#C8C2B4")] // a warm near-grey
    public void ANeutralCover_IsGreyAndDarker(string hex)
    {
        var treatment = TreatmentOf(Hex(hex));

        Assert.Equal(0, treatment.Saturation);
        Assert.Equal(CoverBackdrop.NeutralArtOpacity, treatment.ArtOpacity);
        // No more chroma than the ground's own cool cast gives a grey.
        var now = Tint(Hex(hex), treatment);
        Assert.True(Oklch.Of(now).C < 0.006);
        Assert.True(Oklch.Of(now).L < Oklch.Of(Tint(Hex(hex), new BackdropTreatment(1, CoverBackdrop.ArtOpacity))).L);
    }

    [Theory]
    [InlineData("#D8CFA0")] // pastel yellow
    [InlineData("#C0B890")] // khaki
    [InlineData("#A0B8D8")] // pastel blue
    public void APastelCover_ReadsAsNeutralDark_NotBrown(string hex)
    {
        var cover = Hex(hex);
        var today = Tint(cover, new BackdropTreatment(1, CoverBackdrop.ArtOpacity));
        var now = Tint(cover, TreatmentOf(cover));

        Assert.True(Oklch.Of(now).C < 0.006, $"{hex} leaves {now}, chroma {Oklch.Of(now).C:F4}");
        Assert.True(Oklch.Of(now).C < Oklch.Of(today).C / 2, $"{hex}: {today} → {now}");
        Assert.True(Oklch.Of(now).L <= Oklch.Of(today).L, $"{hex}: {today} → {now}");
    }

    [Fact]
    public void AsChromaRises_TheCoverIsDrawnMoreAsItIs_WithoutJumps()
    {
        var last = CoverBackdrop.For(0);
        for (var c = 0.001; c <= 0.25; c += 0.001)
        {
            var next = CoverBackdrop.For(c);
            Assert.True(next.Saturation >= last.Saturation && next.ArtOpacity >= last.ArtOpacity, $"chroma {c:F3}");
            Assert.True(next.Saturation - last.Saturation < 0.05, $"chroma {c:F3}");
            Assert.True(next.ArtOpacity - last.ArtOpacity < 0.02, $"chroma {c:F3}");
            Assert.InRange(next.Saturation, 0, 1);
            last = next;
        }
        Assert.Equal(CoverBackdrop.For(0), new BackdropTreatment(0, CoverBackdrop.NeutralArtOpacity));
        Assert.Equal(CoverBackdrop.For(1), new BackdropTreatment(1, CoverBackdrop.ArtOpacity));
    }

    [Fact]
    public void Desaturate_KeepsLightness_AndAlpha()
    {
        var rgba = Fill(Hex("#D8CFA0"), 2, alpha: 200);

        CoverBackdrop.Desaturate(rgba, 0);
        Assert.Equal(rgba[0], rgba[1]);
        Assert.Equal(rgba[1], rgba[2]);
        Assert.Equal(200, rgba[3]);
        Assert.InRange(Oklch.Of(new Rgb(rgba[0], rgba[1], rgba[2])).L - Oklch.Of(Hex("#D8CFA0")).L, -0.03, 0.03);

        var kept = Fill(Hex("#D8CFA0"), 2);
        CoverBackdrop.Desaturate(kept, 1);
        Assert.Equal(Fill(Hex("#D8CFA0"), 2), kept);
    }

    [Fact]
    public void Chroma_IgnoresTransparentPixels_AndAnEmptyCoverHasNone()
    {
        var rgba = Fill(Hex("#808080"));
        Fill(Hex("#E03030"), Pixels / 2, alpha: 0).CopyTo(rgba, 0);

        Assert.Equal(0, CoverBackdrop.Chroma(rgba), 2);
        Assert.Equal(0, CoverBackdrop.Chroma([]));
    }
}
