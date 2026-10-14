namespace Rambutan.Core.Visualizer;

/// <summary>
/// Picks the colour a visualizer draws in from an album cover: the cover's most
/// prominent vivid hue, made bright enough to read over Now Playing's dark
/// backdrop. A cover with almost no vivid colour (black and white, grey) gives
/// a light neutral rather than an arbitrary hue.
/// </summary>
public static class ArtworkColor
{
    // 30 hue bins of 12°: narrow enough that a red cover with an orange title
    // stays red, wide enough that a gradient's pixels land together.
    private const int HueBins = 30;

    /// <summary>The vivid weight a cover needs, per opaque pixel, to count as
    /// coloured. A fully saturated, bright pixel weighs 1, so this is about 1% of
    /// the cover in a vivid colour, or more of it in a muted one.</summary>
    private const double MinVividWeight = 0.01;

    /// <summary>The least brightness (HSV value) and saturation of a pick.</summary>
    public const double MinValue = 0.78;
    public const double MinSaturation = 0.45;

    /// <summary>The light neutral for covers without a vivid colour.</summary>
    public static readonly (byte R, byte G, byte B) Neutral = (0xD6, 0xD6, 0xD6);

    /// <summary>
    /// The colour for a cover given as <paramref name="rgba"/>, 4 bytes a pixel
    /// (red, green, blue, alpha; unpremultiplied), any size. Null when no pixel
    /// is at least half opaque.
    /// </summary>
    public static (byte R, byte G, byte B)? Pick(ReadOnlySpan<byte> rgba)
    {
        Span<double> weight = stackalloc double[HueBins];
        Span<double> red = stackalloc double[HueBins];
        Span<double> green = stackalloc double[HueBins];
        Span<double> blue = stackalloc double[HueBins];
        double total = 0, vivid = 0;
        var opaque = 0;

        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 128)
                continue;
            opaque++;
            double r = rgba[i] / 255.0, g = rgba[i + 1] / 255.0, b = rgba[i + 2] / 255.0;
            var (hue, saturation, value) = ToHsv(r, g, b);
            total += 1;
            // Vivid: saturated and not dark. Weighted so a bright, saturated
            // patch outweighs a larger muddy one.
            var w = saturation * saturation * value;
            if (saturation < 0.2 || value < 0.2)
                continue;
            vivid += w;
            var bin = (int)(hue / 360 * HueBins) % HueBins;
            weight[bin] += w;
            red[bin] += r * w;
            green[bin] += g * w;
            blue[bin] += b * w;
        }

        if (opaque == 0)
            return null;
        if (vivid < total * MinVividWeight)
            return Neutral;

        // The strongest hue, counting its neighbours, so a hue split across a
        // bin edge isn't beaten by a smaller one that happens to fit a bin.
        var best = 0;
        var bestScore = -1.0;
        for (var bin = 0; bin < HueBins; bin++)
        {
            var score = weight[bin]
                + 0.5 * weight[(bin + 1) % HueBins]
                + 0.5 * weight[(bin + HueBins - 1) % HueBins];
            if (score > bestScore)
            {
                bestScore = score;
                best = bin;
            }
        }

        double sumWeight = 0, sumR = 0, sumG = 0, sumB = 0;
        for (var offset = -1; offset <= 1; offset++)
        {
            var bin = (best + offset + HueBins) % HueBins;
            sumWeight += weight[bin];
            sumR += red[bin];
            sumG += green[bin];
            sumB += blue[bin];
        }

        var (h, s, v) = ToHsv(sumR / sumWeight, sumG / sumWeight, sumB / sumWeight);
        var (outR, outG, outB) = FromHsv(h, Math.Max(s, MinSaturation), Math.Max(v, MinValue));
        return (ToByte(outR), ToByte(outG), ToByte(outB));
    }

    private static byte ToByte(double channel) => (byte)Math.Round(Math.Clamp(channel, 0, 1) * 255);

    private static (double Hue, double Saturation, double Value) ToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double hue;
        if (delta <= 0)
            hue = 0;
        else if (max == r)
            hue = 60 * ((g - b) / delta % 6);
        else if (max == g)
            hue = 60 * ((b - r) / delta + 2);
        else
            hue = 60 * ((r - g) / delta + 4);
        if (hue < 0)
            hue += 360;
        return (hue, max <= 0 ? 0 : delta / max, max);
    }

    private static (double R, double G, double B) FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = (hue % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (r + m, g + m, b + m);
    }
}
