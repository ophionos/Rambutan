using System;

namespace Rambutan.Core.Layout;

/// <summary>
/// Whether a pane has room for a side column of cards beside its main column,
/// and how wide each then is. The main column keeps the width it asks for; the
/// side column takes the rest. A pane too narrow for both has no side column,
/// and the main column has the pane to itself.
/// </summary>
public static class SideColumnFit
{
    /// <summary>The space kept between the two columns.</summary>
    public const double Gap = 40;

    /// <summary>The side column's narrowest: three cards (176 each, 4 apart)
    /// and a little air, so a shelf never shows fewer.</summary>
    public const double MinSide = 550;

    /// <param name="Show">Whether the side column has room.</param>
    /// <param name="Main">The main column's width.</param>
    /// <param name="Side">The side column's width; 0 when it doesn't show.</param>
    public readonly record struct Result(bool Show, double Main, double Side);

    /// <summary>The narrowest pane that shows a side column beside a main
    /// column <paramref name="main"/> wide.</summary>
    public static double MinPane(double main) => main + Gap + MinSide;

    /// <param name="pane">The width the two columns share.</param>
    /// <param name="main">The width the main column keeps beside a side column.</param>
    public static Result For(double pane, double main)
    {
        if (double.IsFinite(pane) && pane >= MinPane(main))
            return new Result(true, main, pane - main - Gap);
        return new Result(false, double.IsFinite(pane) ? Math.Max(0, pane) : main, 0);
    }
}
