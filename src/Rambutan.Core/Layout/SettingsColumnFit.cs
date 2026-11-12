using System;
using System.Collections.Generic;

namespace Rambutan.Core.Layout;

/// <summary>
/// The Settings columns' width, how many there are, and whether the section
/// list has room beside them. A column grows with the pane up to
/// <see cref="MaxColumn"/>; a pane with room for two columns at their minimum
/// beside the list has two; the list sits at the pane's right edge and never
/// over a column.
/// </summary>
public static class SettingsColumnFit
{
    /// <summary>A column's narrowest while the list shows.</summary>
    public const double MinColumn = 720;

    public const double MaxColumn = 1040;

    public const double ListWidth = 200;

    /// <summary>The space kept between the columns and the list.</summary>
    public const double Gap = 32;

    /// <summary>The space between two columns.</summary>
    public const double ColumnGap = 24;

    /// <summary>The first column's left margin.</summary>
    public const double ColumnLeft = 4;

    /// <summary>The scroller's inset at the right edge, which the list keeps
    /// too so the scrollbar stays clear of it.</summary>
    public const double RightInset = 12;

    /// <summary>The narrowest pane with room for the column at its minimum, the
    /// gap and the list.</summary>
    public const double ListMinPane = ColumnLeft + MinColumn + Gap + ListWidth + RightInset;

    /// <summary>The narrowest pane with room for two columns at their minimum,
    /// the gaps and the list.</summary>
    public const double TwoColumnMinPane = ListMinPane + ColumnGap + MinColumn;

    /// <summary>The column's margins on both sides plus the scroller's inset.</summary>
    private const double ColumnInsets = ColumnLeft + 4 + RightInset;

    /// <param name="Column">One column's width.</param>
    /// <param name="ShowList">Whether the section list shows.</param>
    /// <param name="Columns">How many columns the sections take: 1 or 2.</param>
    public readonly record struct Result(double Column, bool ShowList, int Columns = 1)
    {
        /// <summary>The columns and the gap between them.</summary>
        public double Width => Columns * Column + (Columns - 1) * ColumnGap;
    }

    /// <summary>The columns and list for a pane <paramref name="pane"/> wide.</summary>
    public static Result For(double pane)
    {
        if (pane >= TwoColumnMinPane)
        {
            var room = pane - ColumnLeft - Gap - ListWidth - RightInset;
            return new Result(Math.Clamp((room - ColumnGap) / 2, MinColumn, MaxColumn), true, 2);
        }
        if (pane >= ListMinPane)
        {
            var room = pane - ColumnLeft - Gap - ListWidth - RightInset;
            return new Result(Math.Clamp(room, MinColumn, MaxColumn), true);
        }
        return new Result(Math.Max(0, Math.Min(MaxColumn, pane - ColumnInsets)), false);
    }

    /// <summary>Where the list's left edge is in a pane <paramref name="pane"/> wide.</summary>
    public static double ListLeft(double pane) => pane - RightInset - ListWidth;

    /// <summary>
    /// How many of the sections, in order, go in the first of two columns: the
    /// split that leaves the two columns' heights nearest, so no section is cut
    /// in two and the order reads down the first column, then the second. Of
    /// two splits equally near, the first column is the taller one.
    /// <paramref name="heights"/> are the sections' heights with the space
    /// under each; a hidden section's is 0.
    /// </summary>
    public static int Split(IReadOnlyList<double> heights)
    {
        var total = 0d;
        foreach (var height in heights)
            total += height;

        var best = heights.Count;
        var bestDifference = total;
        var first = 0d;
        for (var count = 0; count < heights.Count; count++)
        {
            // The split after `count` sections.
            var difference = Math.Abs(total - 2 * first);
            if (difference < bestDifference || (difference == bestDifference && first >= total - first))
            {
                best = count;
                bestDifference = difference;
            }
            first += heights[count];
        }
        return best;
    }
}
