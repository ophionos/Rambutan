using System;

namespace Rambutan.App.Views;

/// <summary>
/// Places the title bar's search field in the bar's free room: after the
/// navigation buttons and a gap, before the room the caption buttons are drawn
/// in. It is centred on the whole bar, as a window's title is, and moves off
/// the centre only as far as it must to stay in that room.
/// </summary>
internal static class TitleSearchFit
{
    public const double MinWidth = 240;
    public const double MaxWidth = 420;

    /// <summary>The space kept between the navigation buttons and the field.</summary>
    public const double Gap = 12;

    /// <summary>The caption buttons' room at the bar's right end, as the bar's own
    /// items keep it.</summary>
    public const double CaptionReserve = 150;

    /// <summary>The field's left edge and width in the bar's coordinates, or null
    /// when less than <see cref="MinWidth"/> fits (the field hides).</summary>
    /// <param name="barWidth">The title bar's width.</param>
    /// <param name="navEnd">Where the bar's buttons end, from the bar's left edge.</param>
    public static (double Left, double Width)? Place(double barWidth, double navEnd)
    {
        var start = navEnd + Gap;
        var end = barWidth - CaptionReserve;
        if (end - start < MinWidth)
            return null;
        var width = Math.Min(MaxWidth, end - start);
        var left = Math.Clamp((barWidth - width) / 2, start, end - width);
        return (left, width);
    }
}
