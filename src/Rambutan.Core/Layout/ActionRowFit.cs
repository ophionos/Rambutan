using System;

namespace Rambutan.Core.Layout;

/// <summary>
/// Which actions of a page's action row stay in the row. The row keeps its
/// pinned items whatever the width, then as many of the other actions as fit,
/// in order; the rest go into a More button's menu. The answer follows the
/// widths alone, so an action never changes place on hover or focus.
/// </summary>
public static class ActionRowFit
{
    /// <summary>
    /// The number of leading <paramref name="actions"/> that stay in a row
    /// <paramref name="available"/> wide. All of them when everything fits, and
    /// the More button then doesn't show; otherwise the longest run that fits
    /// beside the More button, which may be none.
    /// </summary>
    /// <param name="available">The row's width.</param>
    /// <param name="pinnedWidth">The total width of the items that always stay.</param>
    /// <param name="pinnedCount">How many those are, for the gaps between items.</param>
    /// <param name="actions">The widths of the actions that may move, in order.</param>
    /// <param name="spacing">The gap between two items.</param>
    /// <param name="moreWidth">The More button's width.</param>
    /// <param name="alwaysMore">The More button shows whatever the width, because
    /// an item lives only in its menu: it takes its room even when every action fits.</param>
    public static int Kept(
        double available,
        double pinnedWidth,
        int pinnedCount,
        ReadOnlySpan<double> actions,
        double spacing,
        double moreWidth,
        bool alwaysMore = false)
    {
        // Half a pixel of slack: measured widths carry rounding from layout.
        var room = available + 0.5;

        var all = pinnedWidth;
        foreach (var width in actions)
            all += width;
        if (alwaysMore)
        {
            if (all + moreWidth + Gaps(pinnedCount + actions.Length + 1, spacing) <= room)
                return actions.Length;
        }
        else if (all + Gaps(pinnedCount + actions.Length, spacing) <= room)
        {
            return actions.Length;
        }

        var kept = actions.Length;
        var used = all;
        while (kept > 0)
        {
            kept--;
            used -= actions[kept];
            if (used + moreWidth + Gaps(pinnedCount + kept + 1, spacing) <= room)
                return kept;
        }

        return 0;
    }

    private static double Gaps(int items, double spacing) => Math.Max(0, items - 1) * spacing;
}
