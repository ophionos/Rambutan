namespace Rambutan.App.Controls;

/// <summary>
/// Scroll arithmetic for paged and endless lists, kept apart from the views so
/// it can be tested without a layout pass.
/// </summary>
public static class ScrollPaging
{
    /// <summary>Offsets this close to an end count as at it (layout rounding).</summary>
    private const double Slack = 0.5;

    /// <summary>
    /// Where a shelf's ‹ or › should scroll to: one visible width
    /// of whole cards, so the card that was cut off at the edge leads the next
    /// page instead of being skipped. The result lands on a card boundary even
    /// when the shelf was left mid-card by a wheel or a scrollbar drag, and is
    /// clamped to the scrollable range.
    /// </summary>
    /// <param name="offset">Current horizontal offset.</param>
    /// <param name="viewport">Visible width.</param>
    /// <param name="extent">Total content width.</param>
    /// <param name="stride">One card plus the spacing after it.</param>
    /// <param name="forward">› when true, ‹ when false.</param>
    public static double PageTarget(double offset, double viewport, double extent, double stride, bool forward)
    {
        var max = Math.Max(0, extent - viewport);
        if (stride <= 0)
            return Math.Clamp(offset + (forward ? viewport : -viewport), 0, max);

        // Cards that fit whole in the viewport (the trailing spacing needn't fit).
        var whole = Math.Max(1, Math.Floor((viewport + Slack) / stride));
        var step = whole * stride;

        var target = forward
            ? Math.Floor((offset + Slack) / stride) * stride + step
            : Math.Ceiling((offset - Slack) / stride) * stride - step;
        return Math.Clamp(target, 0, max);
    }

    /// <summary>True when there is anything to the left to page back to.</summary>
    public static bool CanPageBack(double offset) => offset > Slack;

    /// <summary>True when there is anything to the right still to page to.</summary>
    public static bool CanPageForward(double offset, double viewport, double extent) =>
        offset + viewport < extent - Slack;

    /// <summary>
    /// Continuation paging trigger for endless lists (playlist, Library): the
    /// view asks for the next page once fewer than <paramref name="threshold"/>
    /// pixels of content remain below the viewport.
    /// </summary>
    public static bool IsNearEnd(double offset, double viewport, double extent, double threshold = 400) =>
        extent - offset - viewport < threshold;
}
