using System.Globalization;
using Avalonia.Media;
using Rambutan.LocalApi.Overlay;

namespace Rambutan.App.Services;

/// <summary>
/// The overlay style's colours are CSS hex, with the alpha last
/// (<c>#RRGGBBAA</c>, <c>#RGBA</c>). Avalonia's own parser puts the alpha first
/// (<c>#AARRGGBB</c>), so a CSS colour handed to it straight comes out wrong;
/// everything the app draws from a style goes through here instead.
/// </summary>
public static class CssHexColor
{
    /// <summary>The colour <paramref name="text"/> names, in CSS order; false when
    /// it isn't one of #RGB, #RGBA, #RRGGBB or #RRGGBBAA.</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (!OverlayConfigFormat.IsHexColor(text))
            return false;
        var hex = text![1..];
        if (hex.Length is 3 or 4)
        {
            color = Color.FromArgb(hex.Length == 4 ? Short(hex, 3) : (byte)255, Short(hex, 0), Short(hex, 1), Short(hex, 2));
            return true;
        }
        color = Color.FromArgb(hex.Length == 8 ? Long(hex, 3) : (byte)255, Long(hex, 0), Long(hex, 1), Long(hex, 2));
        return true;
    }

    // #RGB: each digit stands for itself twice (F → FF).
    private static byte Short(string hex, int i) => (byte)(Nibble(hex[i]) * 17);

    private static byte Long(string hex, int i) =>
        byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>The colour, or <paramref name="fallback"/> when the text isn't one.</summary>
    public static Color Parse(string? text, Color fallback) => TryParse(text, out var color) ? color : fallback;

    /// <summary><paramref name="color"/> as the style writes it: #RRGGBB when
    /// solid, else #RRGGBBAA, with the alpha last.</summary>
    public static string Format(Color color) => color.A == 255
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";

    /// <summary>How the style keeps <paramref name="text"/>: trimmed, with the #
    /// added when it was left off, upper case. Null when it isn't a colour.</summary>
    public static string? Normalize(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        if (trimmed[0] != '#')
            trimmed = "#" + trimmed;
        return OverlayConfigFormat.IsHexColor(trimmed) ? trimmed.ToUpperInvariant() : null;
    }

    private static int Nibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => c - 'A' + 10,
    };
}
