using System.Globalization;

namespace Rambutan.Core.Text;

public enum CountScale
{
    Units,
    Thousands,
    Millions,
    Billions,
}

/// <summary>A shortened count: <paramref name="Value"/> of <paramref name="Scale"/>.</summary>
public readonly record struct ShortCount(decimal Value, CountScale Scale);

/// <summary>YouTube Music's short play counts, which it sends as English text.</summary>
public static class PlayCountText
{
    /// <summary>
    /// A count as YouTube Music shortens it: "1.2" billions, "831" millions,
    /// "368" thousands, "812". One decimal below ten of a unit, none above, cut
    /// and never rounded up, so a count read from its text shortens to that text.
    /// </summary>
    public static ShortCount Shorten(long count)
    {
        if (count < 0)
            count = 0;
        var (scale, unit) = count switch
        {
            >= 1_000_000_000 => (CountScale.Billions, 1_000_000_000m),
            >= 1_000_000 => (CountScale.Millions, 1_000_000m),
            >= 1_000 => (CountScale.Thousands, 1_000m),
            _ => (CountScale.Units, 1m),
        };
        var value = count / unit;
        value = value < 10 ? Math.Floor(value * 10) / 10 : Math.Floor(value);
        return new ShortCount(value, scale);
    }

    /// <summary>
    /// "1.2B plays", "674K views", "812 plays" → the number, as rounded as the
    /// text is. Only plays and views: "12 songs" and "1.2M subscribers" count
    /// something else.
    /// </summary>
    public static bool TryParse(string text, out long count)
    {
        count = 0;
        var space = text.LastIndexOf(' ');
        if (space <= 0 || text[(space + 1)..] is not ("play" or "plays" or "view" or "views"))
            return false;

        var number = text.AsSpan(0, space).Trim();
        if (number.IsEmpty)
            return false;
        var scale = number[^1] switch
        {
            'K' => 1_000m,
            'M' => 1_000_000m,
            'B' => 1_000_000_000m,
            _ => 1m,
        };
        if (scale != 1m)
            number = number[..^1];

        const NumberStyles Style =
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands;
        if (!decimal.TryParse(number, Style, CultureInfo.InvariantCulture, out var value) ||
            value < 0 || value * scale > long.MaxValue)
        {
            return false;
        }

        count = (long)(value * scale);
        return true;
    }
}
