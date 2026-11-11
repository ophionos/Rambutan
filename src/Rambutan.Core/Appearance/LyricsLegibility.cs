namespace Rambutan.Core.Appearance;

/// <summary>
/// The opacities that keep lyrics readable where they sit over the visualizer: the
/// stage dim, the scrim behind the lyrics column, and the lyric lines' own opacity.
/// The pane and the stage read these values, and the contrast they give in the worst
/// case (the brightest bar the visualizer can draw) is calculated here, so a change
/// to one that costs legibility fails a test.
/// </summary>
public static class LyricsLegibility
{
    /// <summary>The visualizer's opacity under the lyrics pane.</summary>
    public const double StageDim = 0.3;

    /// <summary>The scrim behind the lyrics column: <see cref="AccentPalette.Ground"/>
    /// at this opacity over the dimmed stage. Low enough that the bars still read as
    /// movement behind it.</summary>
    public const double ScrimOpacity = 0.6;

    /// <summary>A synced line that is neither playing nor next to it.</summary>
    public const double InactiveLineOpacity = 0.5;

    /// <summary>The lines beside the playing one.</summary>
    public const double NearLineOpacity = 0.7;

    /// <summary>Small text on the pane (the source label in the footer).</summary>
    public const double FooterTextOpacity = 0.65;

    /// <summary>The least contrast lyrics text keeps (WCAG 2.2, body text).</summary>
    public const double MinContrast = AccentPalette.MinTextContrast;

    /// <summary>The contrast of white text at <paramref name="textOpacity"/> against the
    /// scrim over a visualizer bar of colour <paramref name="bar"/>, drawn at full
    /// strength in the stage at <see cref="StageDim"/> over the page's ground.</summary>
    public static double TextContrast(Rgb bar, double textOpacity)
    {
        var stage = Blend(bar, AccentPalette.Ground, StageDim);
        var behind = Blend(AccentPalette.Ground, stage, ScrimOpacity);
        var text = Blend(AccentPalette.White, behind, textOpacity);
        return AccentPalette.ContrastRatio(text, behind);
    }

    /// <summary><paramref name="top"/> at <paramref name="opacity"/> over <paramref name="under"/>.</summary>
    private static Rgb Blend(Rgb top, Rgb under, double opacity) => new(
        Channel(top.R * opacity + under.R * (1 - opacity)),
        Channel(top.G * opacity + under.G * (1 - opacity)),
        Channel(top.B * opacity + under.B * (1 - opacity)));

    private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 255));
}
