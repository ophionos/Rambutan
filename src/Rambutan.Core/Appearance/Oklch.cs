namespace Rambutan.Core.Appearance;

/// <summary>A colour in OKLCH: lightness 0–1, chroma (0 for a grey, about 0.32
/// at most in sRGB) and hue in degrees.</summary>
public readonly record struct Lch(double L, double C, double H);

/// <summary>
/// sRGB to and from OKLCH, Björn Ottosson's perceptual space: equal steps of
/// chroma look like equal steps of colourfulness whatever the hue, which HSV's
/// saturation does not (a yellow at half saturation is far duller than a blue).
/// </summary>
public static class Oklch
{
    public static Lch Of(Rgb c)
    {
        var (l, a, b) = Lab(c);
        var hue = Math.Atan2(b, a) * 180 / Math.PI;
        return new(l, Math.Sqrt(a * a + b * b), hue < 0 ? hue + 360 : hue);
    }

    /// <summary>Whether <paramref name="l"/>, <paramref name="c"/>,
    /// <paramref name="h"/> is an sRGB colour.</summary>
    public static bool InGamut(double l, double c, double h)
    {
        const double slack = 1e-4;
        var (r, g, b) = LinearRgb(l, c, h);
        return r is >= -slack and <= 1 + slack
            && g is >= -slack and <= 1 + slack
            && b is >= -slack and <= 1 + slack;
    }

    /// <summary>The nearest 8-bit sRGB colour; a colour out of gamut has its
    /// channels clipped.</summary>
    public static Rgb ToRgb(double l, double c, double h)
    {
        var (r, g, b) = LinearRgb(l, c, h);
        return new(Encode(r), Encode(g), Encode(b));
    }

    /// <summary>The most chroma <paramref name="h"/> has in sRGB at lightness
    /// <paramref name="l"/>.</summary>
    public static double MaxChroma(double l, double h)
    {
        double low = 0, high = 0.4;
        for (var i = 0; i < 24; i++)
        {
            var mid = (low + high) / 2;
            if (InGamut(l, mid, h))
                low = mid;
            else
                high = mid;
        }
        return low;
    }

    /// <summary>The distance between two colours in OKLab (ΔE): about 0.02 is
    /// a difference just seen side by side.</summary>
    public static double Difference(Rgb x, Rgb y)
    {
        var (l1, a1, b1) = Lab(x);
        var (l2, a2, b2) = Lab(y);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    private static (double L, double A, double B) Lab(Rgb c)
    {
        double r = Linear(c.R), g = Linear(c.G), b = Linear(c.B);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static (double R, double G, double B) LinearRgb(double lightness, double chroma, double hue)
    {
        var radians = hue * Math.PI / 180;
        var a = chroma * Math.Cos(radians);
        var b = chroma * Math.Sin(radians);
        var l = lightness + 0.3963377774 * a + 0.2158037573 * b;
        var m = lightness - 0.1055613458 * a - 0.0638541728 * b;
        var s = lightness - 0.0894841775 * a - 1.2914855480 * b;
        l = l * l * l;
        m = m * m * m;
        s = s * s * s;
        return (
            4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    private static double Linear(byte channel)
    {
        var s = channel / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static byte Encode(double linear)
    {
        var x = Math.Clamp(linear, 0, 1);
        var s = x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(s, 0, 1) * 255);
    }
}
