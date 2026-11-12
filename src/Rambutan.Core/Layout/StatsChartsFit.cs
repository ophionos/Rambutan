namespace Rambutan.Core.Layout;

/// <summary>
/// Whether Stats puts the per-day chart and the heatmap side by side. Stretched
/// across a wide pane a day's bar sits alone in a cell many times its width;
/// side by side each chart takes half the width.
/// </summary>
public static class StatsChartsFit
{
    /// <summary>The narrowest a chart is beside the other: a year's 365 bars
    /// keep more than two pixels each.</summary>
    public const double MinHalf = 900;

    /// <summary>The space between the two charts.</summary>
    public const double Gap = 24;

    /// <summary>The narrowest width that holds both charts side by side.</summary>
    public const double MinWidth = 2 * MinHalf + Gap;

    /// <summary>Whether a content width of <paramref name="width"/> holds both
    /// charts side by side. An unbounded width does not: there is no half of it.</summary>
    public static bool SideBySide(double width) => !double.IsInfinity(width) && width >= MinWidth;

    /// <summary>Each chart's width, side by side in <paramref name="width"/>.</summary>
    public static double Half(double width) => (width - Gap) / 2;
}
