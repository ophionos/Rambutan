using System;

namespace Rambutan.Core.Layout;

/// <summary>
/// The Settings column's width and whether the section list has room beside
/// it. The column grows with the pane up to <see cref="MaxColumn"/>; the list
/// sits at the pane's right edge and never over the column.
/// </summary>
public static class SettingsColumnFit
{
    /// <summary>The column's narrowest while the list shows.</summary>
    public const double MinColumn = 720;

    public const double MaxColumn = 1040;

    public const double ListWidth = 200;

    /// <summary>The space kept between the column and the list.</summary>
    public const double Gap = 32;

    /// <summary>The column's left margin.</summary>
    public const double ColumnLeft = 4;

    /// <summary>The scroller's inset at the right edge, which the list keeps
    /// too so the scrollbar stays clear of it.</summary>
    public const double RightInset = 12;

    /// <summary>The narrowest pane with room for the column at its minimum, the
    /// gap and the list.</summary>
    public const double ListMinPane = ColumnLeft + MinColumn + Gap + ListWidth + RightInset;

    /// <summary>The column's margins on both sides plus the scroller's inset.</summary>
    private const double ColumnInsets = ColumnLeft + 4 + RightInset;

    /// <param name="Column">The column's width.</param>
    /// <param name="ShowList">Whether the section list shows.</param>
    public readonly record struct Result(double Column, bool ShowList);

    /// <summary>The column and list for a pane <paramref name="pane"/> wide.</summary>
    public static Result For(double pane)
    {
        if (pane >= ListMinPane)
        {
            var room = pane - ColumnLeft - Gap - ListWidth - RightInset;
            return new Result(Math.Clamp(room, MinColumn, MaxColumn), true);
        }
        return new Result(Math.Max(0, Math.Min(MaxColumn, pane - ColumnInsets)), false);
    }

    /// <summary>Where the list's left edge is in a pane <paramref name="pane"/> wide.</summary>
    public static double ListLeft(double pane) => pane - RightInset - ListWidth;
}
