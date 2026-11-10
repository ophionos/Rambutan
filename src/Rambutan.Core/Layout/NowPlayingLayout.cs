namespace Rambutan.Core.Layout;

/// <summary>
/// Sizes Now Playing's artwork and title from the room the stage gives them.
/// The artwork takes a share of the room, so the stage keeps some air around
/// it, between a floor where a cover still reads and a cap; the title grows
/// with it, as a display size.
/// </summary>
public static class NowPlayingLayout
{
    /// <summary>The artwork's share of its room. Below 1, so the artwork and
    /// the title under it always fit the height the room was taken from.</summary>
    public const double Share = 0.8;

    /// <summary>The smallest artwork; on a stage too short for it, it overflows
    /// rather than shrinking past recognition.</summary>
    public const double MinArt = 120;

    public const double MaxArt = 480;

    /// <summary>The cap in fullscreen, where the screen is larger.</summary>
    public const double FullscreenMaxArt = 640;

    /// <summary>The cover's side in the compact header above the lyrics.</summary>
    public const double HeaderArt = 64;

    /// <summary>The title's size in the compact header and over the lyrics alone.</summary>
    public const double CompactTitleSize = 20;

    public const double MinTitleSize = 24;
    public const double MaxTitleSize = 32;

    /// <summary>The artwork sizes at which the title stops growing or shrinking.</summary>
    private const double SmallArt = 300;
    private const double LargeArt = 420;

    /// <summary>The width the cover is decoded at: the largest artwork
    /// (<see cref="FullscreenMaxArt"/>) on a display scaled to 150 %. A larger
    /// scale draws it slightly soft rather than decoding every cover larger.</summary>
    public const int DecodeWidth = 960;

    /// <summary>The artwork's side in a square of <paramref name="room"/>, with
    /// <paramref name="ring"/> kept clear on every side for the radial
    /// visualizer (0 without it).</summary>
    public static double ArtSize(double room, double ring, bool fullscreen) =>
        Math.Clamp(Share * room - 2 * ring, MinArt, fullscreen ? FullscreenMaxArt : MaxArt);

    /// <summary>The title's font size under an artwork of <paramref name="artSize"/>:
    /// <see cref="MinTitleSize"/> up to a small artwork, <see cref="MaxTitleSize"/>
    /// from a large one, in whole pixels between.</summary>
    public static double TitleSize(double artSize)
    {
        var t = Math.Clamp((artSize - SmallArt) / (LargeArt - SmallArt), 0, 1);
        return Math.Round(MinTitleSize + t * (MaxTitleSize - MinTitleSize), MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The artwork and title for a stage <paramref name="width"/> wide (the
    /// width the artwork may take) and <paramref name="height"/> high.
    /// <paramref name="textRoom"/> gives the height under the artwork that a
    /// title of a font size takes, with the artist line. The title's room
    /// takes from the artwork's, and the title follows the artwork: the
    /// artwork is first sized beside the largest title, the title follows
    /// that artwork, and the artwork is sized again beside that title, so the
    /// title's room is the one it was sized with.
    /// </summary>
    public static NowPlayingFit Fit(double width, double height, double ring, bool fullscreen,
        Func<double, double> textRoom)
    {
        var first = ArtSize(Math.Min(width, height - textRoom(MaxTitleSize)), ring, fullscreen);
        var title = TitleSize(first);
        var art = ArtSize(Math.Min(width, height - textRoom(title)), ring, fullscreen);
        return new NowPlayingFit(art, title);
    }
}

/// <param name="Art">The artwork's side, without the ring around it.</param>
/// <param name="TitleSize">The title's font size.</param>
public readonly record struct NowPlayingFit(double Art, double TitleSize);
