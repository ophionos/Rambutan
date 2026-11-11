namespace Rambutan.Core.Layout;

/// <summary>The header's state and the list's offset after a step of
/// <see cref="HeroCollapse"/>.</summary>
public readonly record struct HeroStep(bool Collapsed, double Offset);

/// <summary>
/// When a page's hero (cover, title, actions) gives way to a thin bar above
/// its track list, and back. The hero takes its own row above the list, so
/// collapsing it makes the list's viewport taller by the difference between
/// the two (<c>delta</c>), and the list's top moves up by as much.
/// <list type="bullet">
/// <item>Expanded, it collapses once the list is scrolled at least
/// <c>delta</c> + <see cref="Slack"/>, and the offset drops by <c>delta</c>,
/// so the rows stay where they are on screen. A list that can't scroll that
/// far never collapses, which includes every list that fits beside the hero.</item>
/// <item>Collapsed, it expands only at the top (<see cref="Top"/>), and the
/// offset stays: the hero coming back pushes the list down.</item>
/// </list>
/// After a collapse the offset is at least <see cref="Slack"/>, and the
/// collapsed list can scroll exactly <c>delta</c> less than the expanded one
/// did, so a collapse never lands where the rule would expand it again.
/// </summary>
public static class HeroCollapse
{
    /// <summary>How far past <c>delta</c> the list scrolls before the hero
    /// collapses: the gap between the two thresholds, so a list that just
    /// collapsed sits clear of the top.</summary>
    public const double Slack = 24;

    /// <summary>The offset at or under which the list counts as at its top.</summary>
    public const double Top = 1;

    /// <summary>
    /// The step after the list scrolled, or its extent or viewport changed.
    /// <paramref name="viewport"/> is the list's viewport in the current state;
    /// the heights include the margins under the hero and the bar.
    /// <paramref name="holdExpanded"/> keeps the hero up (a state that shows
    /// only in it).
    /// </summary>
    public static HeroStep Scrolled(
        bool collapsed, double offset, double extent, double viewport,
        double heroHeight, double barHeight, bool holdExpanded)
    {
        if (holdExpanded)
            return new HeroStep(false, offset);
        if (collapsed)
            return new HeroStep(offset > Top, offset);

        var delta = heroHeight - barHeight;
        if (delta <= 0)
            return new HeroStep(false, offset);

        var threshold = delta + Slack;
        return offset >= threshold && extent - viewport >= threshold
            ? new HeroStep(true, offset - delta)
            : new HeroStep(false, offset);
    }

    /// <summary>
    /// The state for a page shown again at an offset it remembered, measured
    /// with the hero expanded (<paramref name="viewport"/> is the expanded one).
    /// A scrolled page shows the bar at that same offset, as it was most likely
    /// left, unless the collapsed list can't scroll that far.
    /// </summary>
    public static HeroStep Restored(
        double offset, double extent, double viewport,
        double heroHeight, double barHeight, bool holdExpanded)
    {
        var delta = heroHeight - barHeight;
        if (holdExpanded || offset <= Top || delta <= 0)
            return new HeroStep(false, offset);

        var collapsedRoom = extent - (viewport + delta);
        return new HeroStep(collapsedRoom >= offset - 0.5, offset);
    }
}
