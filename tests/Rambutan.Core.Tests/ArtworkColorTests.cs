using Rambutan.Core.Visualizer;

namespace Rambutan.Core.Tests;

/// <summary>The visualizer's colour from a cover: its vivid hue, bright enough
/// for the dark backdrop, or a neutral when it has none.</summary>
public class ArtworkColorTests
{
    private const int Pixels = 32 * 32;

    private static byte[] Fill(byte r, byte g, byte b, byte a = 255, int count = Pixels)
    {
        var rgba = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            rgba[i * 4] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = a;
        }
        return rgba;
    }

    private static (double Hue, double Saturation, double Value) Hsv((byte R, byte G, byte B) c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        var hue = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (hue < 0 ? hue + 360 : hue, max == 0 ? 0 : d / max, max);
    }

    [Fact]
    public void ASolidColour_IsItsOwnPick()
    {
        var pick = ArtworkColor.Pick(Fill(0xE0, 0x30, 0x30));
        Assert.Equal(((byte)0xE0, (byte)0x30, (byte)0x30), pick);
    }

    [Fact]
    public void ADarkCover_IsBrightened_KeepingItsHue()
    {
        // Navy: hue 240, too dark to read over the backdrop.
        var pick = ArtworkColor.Pick(Fill(0x10, 0x18, 0x60))!.Value;

        var (hue, saturation, value) = Hsv(pick);
        Assert.InRange(hue, 225, 240);
        Assert.True(value >= ArtworkColor.MinValue - 0.01);
        Assert.True(saturation >= ArtworkColor.MinSaturation - 0.01);
    }

    [Fact]
    public void AVividPatch_WinsOverALargerGreyArea()
    {
        // Three quarters grey, a quarter saturated blue.
        var rgba = Fill(0x80, 0x80, 0x80);
        Fill(0x20, 0x60, 0xF0, count: Pixels / 4).CopyTo(rgba, 0);

        var (hue, _, _) = Hsv(ArtworkColor.Pick(rgba)!.Value);
        Assert.InRange(hue, 215, 230);
    }

    [Fact]
    public void TheLargerOfTwoHues_Wins()
    {
        var rgba = Fill(0xF0, 0x40, 0x30); // red-orange, two thirds
        Fill(0x30, 0xC0, 0x40, count: Pixels / 3).CopyTo(rgba, 0); // green, a third

        var (hue, _, _) = Hsv(ArtworkColor.Pick(rgba)!.Value);
        Assert.InRange(hue, 0, 15);
    }

    [Fact]
    public void AGreyscaleCover_GivesTheNeutral()
    {
        var rgba = Fill(0x20, 0x20, 0x20);
        Fill(0xF0, 0xF0, 0xF0, count: Pixels / 2).CopyTo(rgba, 0);

        Assert.Equal(ArtworkColor.Neutral, ArtworkColor.Pick(rgba));
    }

    [Fact]
    public void AGreyCoverWithATinySpeckOfColour_GivesTheNeutral()
    {
        // 4 red pixels of 1024: a logo in a corner, not the cover's colour.
        var rgba = Fill(0x60, 0x60, 0x60);
        Fill(0xF0, 0x20, 0x20, count: 4).CopyTo(rgba, 0);

        Assert.Equal(ArtworkColor.Neutral, ArtworkColor.Pick(rgba));
    }

    [Fact]
    public void TransparentPixels_DontCount()
    {
        var rgba = Fill(0xF0, 0x20, 0x20, a: 0);
        Fill(0x20, 0xC0, 0x40, count: 10).CopyTo(rgba, 0);

        var (hue, _, _) = Hsv(ArtworkColor.Pick(rgba)!.Value);
        Assert.InRange(hue, 115, 140);
    }

    [Fact]
    public void AFullyTransparentCover_HasNoPick()
    {
        Assert.Null(ArtworkColor.Pick(Fill(0xF0, 0x20, 0x20, a: 0)));
        Assert.Null(ArtworkColor.Pick([]));
    }
}
