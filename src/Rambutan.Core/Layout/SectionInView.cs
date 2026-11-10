namespace Rambutan.Core.Layout;

/// <summary>
/// Which section of a scrolled page a list of its sections marks as in view.
/// </summary>
public static class SectionInView
{
    /// <summary>How far under the viewport's top a heading may sit and still
    /// count as reached: a heading scrolled to the top lands within a fraction of
    /// a pixel of it, and one just under it already leads the view.</summary>
    public const double TopMargin = 8;

    /// <summary>
    /// The index of the section in view, or -1 when there are no headings.
    /// <paramref name="headingTops"/> are the headings' tops in the content's
    /// coordinates, in ascending order. The rule: the last heading at or above
    /// the viewport's top (plus <see cref="TopMargin"/>); the first section
    /// before any heading is reached; and the last section once the scroll is
    /// at its end, since a short last section can never reach the top. Content
    /// that fits the viewport doesn't scroll, so it marks by the first rule.
    /// </summary>
    public static int Index(IReadOnlyList<double> headingTops, double offset, double viewport, double extent)
    {
        if (headingTops.Count == 0)
            return -1;

        var scrollable = extent - viewport;
        if (scrollable > 1 && offset >= scrollable - 1)
            return headingTops.Count - 1;

        var index = 0;
        for (var i = 0; i < headingTops.Count; i++)
        {
            if (headingTops[i] <= offset + TopMargin)
                index = i;
            else
                break;
        }
        return index;
    }
}
