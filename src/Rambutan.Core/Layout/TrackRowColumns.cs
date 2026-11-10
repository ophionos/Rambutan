namespace Rambutan.Core.Layout;

/// <summary>
/// Which of a track row's optional columns show at a list's width, and how wide.
/// The title never gives up room to them: a column shows only while the title
/// keeps <see cref="MinTitle"/>.
/// </summary>
public static class TrackRowColumns
{
    /// <summary>The room the title block always keeps.</summary>
    public const double MinTitle = 320;

    /// <summary>Narrower than this, an album name is mostly ellipsis, so the column hides.</summary>
    public const double MinAlbum = 160;

    public const double MaxAlbum = 320;

    /// <summary>The album column's share of the list at most, so on a mid-sized
    /// list the title stays the widest part of the row.</summary>
    public const double MaxAlbumShare = 0.3;

    /// <summary>
    /// The album column's width for a list <paramref name="listWidth"/> wide whose
    /// other fixed parts (number, cover, actions, duration, the gaps between them)
    /// take <paramref name="fixedWidth"/>; 0 when the column doesn't show, which
    /// includes a width that isn't a finite positive number.
    /// </summary>
    public static double AlbumWidth(double listWidth, double fixedWidth)
    {
        if (!double.IsFinite(listWidth) || !double.IsFinite(fixedWidth) || listWidth <= 0)
            return 0;

        var room = listWidth - fixedWidth - MinTitle;
        if (room < MinAlbum)
            return 0;

        return Math.Max(MinAlbum, Math.Min(room, Math.Min(MaxAlbum, listWidth * MaxAlbumShare)));
    }
}
