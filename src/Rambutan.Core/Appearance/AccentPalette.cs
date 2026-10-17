namespace Rambutan.Core.Appearance;

/// <summary>An sRGB colour, 8 bits a channel.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>As #RRGGBB, upper case.</summary>
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Every colour the app derives from its accent: the Fluent ramp its controls
/// read, the accent buttons' states, and the colour of text drawn on the accent.
/// </summary>
/// <param name="OnAccent">Text and glyphs on an accent fill: white, or the near
/// black of the window when white would be too faint.</param>
public sealed record AccentPalette(
    Rgb Base,
    Rgb Light1, Rgb Light2, Rgb Light3,
    Rgb Dark1, Rgb Dark2, Rgb Dark3,
    Rgb Hover, Rgb Pressed,
    Rgb OnAccent)
{
    public static readonly Rgb White = new(0xFF, 0xFF, 0xFF);

    /// <summary>The window's ground, for dark text on a light accent.</summary>
    public static readonly Rgb Ground = new(0x0A, 0x0A, 0x0C);

    /// <summary>A panel over the ground, the surface most accent text sits on.</summary>
    public static readonly Rgb Panel = new(0x14, 0x14, 0x18);

    /// <summary>The least contrast accent text keeps against <see cref="Panel"/>
    /// (WCAG 2.2's 4.5:1 for body text).</summary>
    public const double MinTextContrast = 4.5;

    /// <summary>White text stays on an accent fill while it reaches this
    /// contrast. 3:1, not 4.5:1: the default red gives white 3.3:1, and its
    /// white buttons are the app's look; the fills carry short, semibold labels
    /// and glyphs.</summary>
    public const double MinWhiteOnAccent = 3.0;

    /// <summary>The app's own accent, with the hand-tuned values it has always
    /// had. A derived palette is close to these but not equal.</summary>
    public static readonly AccentPalette Default = new(
        Base: new(0xFF, 0x4E, 0x45),
        Light1: new(0xFF, 0x6E, 0x66), Light2: new(0xFF, 0x8D, 0x86), Light3: new(0xFF, 0xAD, 0xA7),
        Dark1: new(0xD9, 0x42, 0x3B), Dark2: new(0xB3, 0x37, 0x31), Dark3: new(0x8C, 0x2B, 0x26),
        Hover: new(0xFF, 0x61, 0x57), Pressed: new(0xE5, 0x42, 0x3A),
        OnAccent: White);

    /// <summary>
    /// The palette for a chosen colour. The default red gives
    /// <see cref="Default"/> exactly. Any other colour is first lightened until
    /// text in it reads on a panel (<see cref="Readable"/>), then the ramp is
    /// derived with the default's spacing: towards white for the light steps,
    /// towards black for the dark ones.
    /// </summary>
    public static AccentPalette From(Rgb chosen)
    {
        if (chosen == Default.Base)
            return Default;
        var color = Readable(chosen);
        return new(
            Base: color,
            Light1: Mix(color, White, 0.18), Light2: Mix(color, White, 0.355), Light3: Mix(color, White, 0.53),
            Dark1: Scale(color, 0.85), Dark2: Scale(color, 0.70), Dark3: Scale(color, 0.55),
            Hover: Mix(color, White, 0.10), Pressed: Scale(color, 0.88),
            OnAccent: ContrastRatio(White, color) >= MinWhiteOnAccent ? White : Ground);
    }

    /// <summary><paramref name="color"/>, or the least lightening of it towards
    /// white that reaches <see cref="MinTextContrast"/> against <see cref="Panel"/>.</summary>
    public static Rgb Readable(Rgb color)
    {
        if (ContrastRatio(color, Panel) >= MinTextContrast)
            return color;
        // Contrast against a dark panel only grows towards white: a bisection
        // finds the least mix that passes.
        double low = 0, high = 1;
        for (var i = 0; i < 16; i++)
        {
            var mid = (low + high) / 2;
            if (ContrastRatio(Mix(color, White, mid), Panel) >= MinTextContrast)
                high = mid;
            else
                low = mid;
        }
        return Mix(color, White, high);
    }

    /// <summary>WCAG 2.2's contrast ratio, from 1 (the same) to 21 (black on white).</summary>
    public static double ContrastRatio(Rgb a, Rgb b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>WCAG 2.2's relative luminance.</summary>
    public static double Luminance(Rgb c) =>
        0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Linear(byte channel)
    {
        var s = channel / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static Rgb Mix(Rgb from, Rgb to, double amount) => new(
        Channel(from.R + (to.R - from.R) * amount),
        Channel(from.G + (to.G - from.G) * amount),
        Channel(from.B + (to.B - from.B) * amount));

    private static Rgb Scale(Rgb c, double factor) =>
        new(Channel(c.R * factor), Channel(c.G * factor), Channel(c.B * factor));

    private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 255));
}
