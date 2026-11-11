using System;

namespace Rambutan.App.Views;

/// <summary>
/// Sizes the player bar's seek line. The bar's side columns are equal stars around
/// the transport, so a seek line of fixed width left too little room at the sides
/// in a narrow window: at ~1000 px the title disappeared, the rating buttons drew
/// over the artist and the volume slider ran off the edge. The seek line gives way
/// first instead, down to a floor where it is still a usable target. Below that the
/// bar turns compact: rating and save fold into one More button, the volume slider
/// into the volume button, and the title may trim further. The window's minimum is
/// the compact bar at the floor, but a tiling window manager can ignore it, so the
/// bar also has a narrow fit for windows below that: the times move under the seek
/// line and the title can trim away entirely (<see cref="Fit"/>).
/// </summary>
internal static class PlayerBarLayout
{
    /// <summary>The widest the seek line grows, in a window with room to spare.</summary>
    public const double MaxSeekWidth = 720;

    /// <summary>The seek line's width in the mini player, which does not grow with
    /// its window.</summary>
    public const double MiniMaxSeekWidth = 460;

    /// <summary>The seek line's width up to which the room beyond the usual goes to it
    /// alone; past it the room is shared with the identity's text
    /// (<see cref="IdentityTextWidth"/>).</summary>
    public const double SeekShareStart = 460;

    public const double MinSeekWidth = 160;

    /// <summary>The narrowest the title may get before the seek line stops giving way.</summary>
    public const double MinTitleWidth = 120;

    /// <summary>The same, in a compact bar.</summary>
    public const double CompactTitleWidth = 80;

    /// <summary>The most a long title takes from the seek line in a full bar.</summary>
    public const double MaxTitleWidth = 320;

    /// <summary>The room a full bar keeps for a title <paramref name="titleWidth"/> wide
    /// untrimmed: the seek line gives way to it as it does to a narrow window, up to
    /// <see cref="MaxTitleWidth"/>, and never below <see cref="MinTitleWidth"/>. Only the
    /// seek line follows it; which bar fits doesn't, so a title can't fold the bar.</summary>
    public static double TitleRoom(double titleWidth) =>
        Math.Clamp(titleWidth, MinTitleWidth, MaxTitleWidth);

    /// <summary>The seek line's width for a bar <paramref name="barWidth"/> wide, whose
    /// side zones each need <paramref name="sideNeed"/> (the wider of the two, since
    /// the columns are equal) and whose time labels take <paramref name="timeLabels"/>.</summary>
    public static double SeekWidth(double barWidth, double sideNeed, double timeLabels, double maxWidth = MaxSeekWidth) =>
        Math.Clamp(barWidth - 2 * sideNeed - timeLabels, MinSeekWidth, maxWidth);

    /// <summary>The width of the full bar's title and byline column, which Like,
    /// Dislike and Save follow, so they stay put from track to track. It depends on
    /// the bar and the fit, never on the title: the floor (<see cref="MinTitleWidth"/>)
    /// while the seek line is below <see cref="SeekShareStart"/>, then a quarter of
    /// the room beyond that, up to <see cref="MaxTitleWidth"/> (the seek line keeps the
    /// other three quarters). Null where the column is free to trim, in a compact or
    /// narrow bar. <paramref name="sideNeed"/> is the side zones' need with the title
    /// at its floor.</summary>
    public static double? IdentityTextWidth(double barWidth, double sideNeed, double timeLabels, PlayerBarFit fit)
    {
        if (fit != PlayerBarFit.Full)
            return null;
        var spare = barWidth - 2 * sideNeed - timeLabels - SeekShareStart;
        return Math.Clamp(MinTitleWidth + spare / 4, MinTitleWidth, MaxTitleWidth);
    }

    /// <summary>The narrowest bar in which both side zones keep what they need around
    /// a seek line at its floor; the inverse of <see cref="SeekWidth"/> at the floor.</summary>
    public static double MinBarWidth(double sideNeed, double timeLabels) =>
        2 * sideNeed + timeLabels + MinSeekWidth;

    /// <summary>Which bar fits <paramref name="barWidth"/>: the full one while its seek
    /// line is at or above the floor, then the compact one, then the narrow one.
    /// The side needs are the full and compact bars' (title at
    /// <see cref="MinTitleWidth"/> and <see cref="CompactTitleWidth"/>). They don't
    /// depend on which bar shows, so the fit can't flip back and forth on its own.</summary>
    public static PlayerBarFit Fit(double barWidth, double fullSide, double compactSide, double timeLabels) =>
        barWidth >= MinBarWidth(fullSide, timeLabels) - 0.5 ? PlayerBarFit.Full
        : barWidth >= MinBarWidth(compactSide, timeLabels) - 0.5 ? PlayerBarFit.Compact
        : PlayerBarFit.Narrow;

    /// <summary>The window's MinWidth (or MinHeight) for a window <paramref name="windowSize"/>
    /// wide (or high) whose layout needs <paramref name="minimum"/>. The minimum is the
    /// window manager's hint, but it also holds the layout at that size, and a tiling
    /// window manager may ignore the hint: the layout then ran past the window, cutting
    /// off the bar's right end and the caption buttons. So it holds only while the
    /// window is that big; below, the layout follows the window, the bar takes its
    /// narrow fit and the sidebar's list scrolls.</summary>
    public static double HeldMinimum(double windowSize, double minimum) =>
        windowSize >= minimum - 0.5 ? minimum : 0;
}

internal enum PlayerBarFit
{
    Full,
    Compact,
    /// <summary>Narrower than the window's minimum, which only a window manager that
    /// ignores it allows: compact, the times under the seek line, the title free to trim away.</summary>
    Narrow,
}
