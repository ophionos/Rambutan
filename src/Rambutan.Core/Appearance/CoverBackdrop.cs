namespace Rambutan.Core.Appearance;

/// <summary>How the page background draws a cover.</summary>
/// <param name="Saturation">1 draws the cover's colours as they are, 0 as greys
/// of the same lightness.</param>
/// <param name="ArtOpacity">The cover's opacity under the scrim.</param>
public readonly record struct BackdropTreatment(double Saturation, double ArtOpacity);

/// <summary>
/// The page background's treatment of a cover. A cover with little colour, a
/// pastel above all, blurs to a light olive or khaki that the scrim darkens to
/// brown, and the translucent panels over it look dirty rather than tinted. So
/// the weaker the cover's colour, the greyer and darker it is drawn, until a
/// cover with no usable colour reads as a neutral dark. A vivid cover is drawn
/// as it always was.
/// </summary>
public static class CoverBackdrop
{
    /// <summary>A vivid cover's opacity: the value the layer has always had.</summary>
    public const double ArtOpacity = 0.85;

    /// <summary>A colourless cover's opacity: darker, so a light grey cover
    /// does not raise the whole window to a mid grey.</summary>
    public const double NeutralArtOpacity = 0.6;

    /// <summary>The cover chroma (<see cref="Chroma"/>) at and under which it is
    /// drawn grey: a pastel yellow, #D8CFA0, has 0.063, a pastel blue 0.053.</summary>
    public const double NeutralChroma = 0.05;

    /// <summary>The cover chroma from which it is drawn as it is. Low, so
    /// that an ordinary cover with a patch of real colour keeps it.</summary>
    public const double VividChroma = 0.10;

    /// <summary>The share of a cover that has to reach a chroma for it to
    /// count: a tenth, so a patch of colour the blur keeps counts, and a speck
    /// does not.</summary>
    private const double Share = 0.1;

    private const int Bins = 100;
    private const double BinWidth = 0.004;

    /// <summary>
    /// The cover's colourfulness: the OKLCH chroma that a tenth of its pixels
    /// reach. <paramref name="rgba"/> is 4 bytes a pixel (red, green, blue,
    /// alpha; unpremultiplied); pixels less than half opaque don't count. 0
    /// when none does.
    /// </summary>
    public static double Chroma(ReadOnlySpan<byte> rgba)
    {
        Span<int> histogram = stackalloc int[Bins];
        var counted = 0;
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 128)
                continue;
            var chroma = Oklch.Of(new Rgb(rgba[i], rgba[i + 1], rgba[i + 2])).C;
            histogram[Math.Min(Bins - 1, (int)(chroma / BinWidth))]++;
            counted++;
        }
        if (counted == 0)
            return 0;
        var needed = Math.Max(1, (int)Math.Ceiling(counted * Share));
        for (var bin = Bins - 1; bin >= 0; bin--)
        {
            needed -= histogram[bin];
            if (needed <= 0)
                return bin * BinWidth;
        }
        return 0;
    }

    /// <summary>The treatment for a cover of <paramref name="chroma"/>
    /// (<see cref="Chroma"/>): grey and at <see cref="NeutralArtOpacity"/> up to
    /// <see cref="NeutralChroma"/>, as it is from <see cref="VividChroma"/>, and
    /// a smooth blend between, so two similar covers look alike.</summary>
    public static BackdropTreatment For(double chroma)
    {
        var t = Math.Clamp((chroma - NeutralChroma) / (VividChroma - NeutralChroma), 0, 1);
        var smooth = t * t * (3 - 2 * t);
        return new(smooth, NeutralArtOpacity + (ArtOpacity - NeutralArtOpacity) * smooth);
    }

    /// <summary>Mixes each pixel of <paramref name="rgba"/> (as for
    /// <see cref="Chroma"/>) towards the grey of its luma by 1 −
    /// <paramref name="saturation"/>, in place. Alpha is kept.</summary>
    public static void Desaturate(Span<byte> rgba, double saturation)
    {
        if (saturation >= 1)
            return;
        var keep = Math.Clamp(saturation, 0, 1);
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            double r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            var grey = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            rgba[i] = Channel(grey + (r - grey) * keep);
            rgba[i + 1] = Channel(grey + (g - grey) * keep);
            rgba[i + 2] = Channel(grey + (b - grey) * keep);
        }
    }

    private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 255));
}
