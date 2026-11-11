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

    /// <summary>Wider than this, a title is mostly empty room that pushes the
    /// columns after it away from it.</summary>
    public const double MaxTitle = 520;

    /// <summary>The liked mark's slot.</summary>
    public const double Liked = 24;

    /// <summary>The room before the album column (<c>Panel.rowalbum</c>'s margin),
    /// taken only while the column shows.</summary>
    public const double AlbumGap = 16;

    /// <summary>The plays column with the room before it: "1.8B plays" in the
    /// meta size, and 16 px.</summary>
    public const double Plays = 104;

    /// <summary>
    /// A row whose columns sit at fixed positions: the liked mark, then the album,
    /// then the plays, each only while the title keeps <see cref="MinTitle"/> and
    /// the ones before it show, so widening never removes one. Above the minimums, the title and the album share the spare
    /// width in proportion to what each lacks of its maximum, so both reach it
    /// together; past that the row ends, and what follows the title (the actions,
    /// the duration) stays at a fixed distance from it instead of at the far edge.
    /// </summary>
    /// <param name="rowWidth">The room a row has: the list less its padding and the item's.</param>
    /// <param name="fixedWidth">What the row's other parts take (number or cover,
    /// marker, actions, duration and their gaps), without the liked mark, the album
    /// and the plays.</param>
    /// <returns>The row's widths; <see cref="TrackRowFit.Content"/> is NaN (unsized)
    /// for a width that isn't a finite positive number.</returns>
    public static TrackRowFit Fit(
        double rowWidth, double fixedWidth, bool hasAlbum, bool hasLiked, bool hasPlays = false)
    {
        if (!double.IsFinite(rowWidth) || !double.IsFinite(fixedWidth) || rowWidth <= 0)
            return new TrackRowFit(0, 0, false, double.NaN);

        var room = Math.Max(0, rowWidth - fixedWidth);
        var liked = hasLiked && room - Liked >= MinTitle;
        if (liked)
            room -= Liked;

        // The plays come last: only once every column before them has its minimum.
        var albumFits = hasAlbum && (liked || !hasLiked) && room - AlbumGap - MinAlbum >= MinTitle;
        var plays = hasPlays && (liked || !hasLiked) && (albumFits || !hasAlbum) &&
            room - Plays - (hasAlbum ? AlbumGap + MinAlbum : 0) >= MinTitle;
        if (plays)
            room -= Plays;

        double title;
        var album = 0.0;
        if (albumFits)
        {
            const double titleLack = MaxTitle - MinTitle;
            const double albumLack = MaxAlbum - MinAlbum;
            var spare = Math.Min(room - AlbumGap - MinAlbum - MinTitle, titleLack + albumLack);
            title = MinTitle + spare * titleLack / (titleLack + albumLack);
            album = MinAlbum + spare * albumLack / (titleLack + albumLack);
        }
        else
        {
            title = Math.Min(room, MaxTitle);
        }

        var content = fixedWidth + title + (liked ? Liked : 0) + (album > 0 ? AlbumGap + album : 0) +
            (plays ? Plays : 0);
        return new TrackRowFit(title, album, liked, Math.Min(content, rowWidth), plays);
    }
}

/// <summary>A row's widths from <see cref="TrackRowColumns.Fit"/>.</summary>
/// <param name="Title">The title block's width.</param>
/// <param name="Album">The album column's width, without its gap; 0 while it doesn't show.</param>
/// <param name="ShowsLiked">Whether the liked mark's slot shows.</param>
/// <param name="Content">The whole row's width, at most the room it was given;
/// NaN when unsized.</param>
/// <param name="ShowsPlays">Whether the plays column shows.</param>
public readonly record struct TrackRowFit(
    double Title, double Album, bool ShowsLiked, double Content, bool ShowsPlays = false)
{
    public bool ShowsAlbum => Album > 0;
}
