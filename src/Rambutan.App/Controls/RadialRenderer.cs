using System;
using SkiaSharp;

namespace Rambutan.App.Controls;

/// <summary>
/// Spokes around a square in the middle of the area: the Now Playing artwork,
/// drawn over them. The lows sit at the bottom and the spectrum climbs both
/// sides to the highs at the top, mirrored. The area is the square plus
/// <see cref="Margin"/> on every side.
/// </summary>
internal sealed class RadialRenderer : IVisualizerRenderer
{
    /// <summary>The space between the square and the spokes, in DIPs.</summary>
    internal const float Gap = 10;

    /// <summary>The longest a spoke gets, in DIPs.</summary>
    internal const float Extent = 40;

    /// <summary>How far the area reaches past the square on each side.</summary>
    internal const float Margin = Gap + Extent;

    private readonly SKPaint _spoke = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeCap = SKStrokeCap.Round,
    };

    public VisualizerInputs Inputs => VisualizerInputs.None;

    /// <summary>A spoke spans <see cref="Extent"/>, not the area's height.</summary>
    public float LevelReach(SKSize size) => Extent;

    public bool IsAnimated => false;

    /// <summary>The direction of spoke <paramref name="index"/> of
    /// <paramref name="count"/> on one side, as a unit vector with y down:
    /// index 0 next to straight down, the last next to straight up; the right
    /// side, or the left when <paramref name="mirrored"/>.</summary>
    internal static (float X, float Y) Direction(int index, int count, bool mirrored)
    {
        var angle = (index + 0.5) / count * Math.PI;
        var x = (float)Math.Sin(angle);
        return (mirrored ? -x : x, (float)Math.Cos(angle));
    }

    /// <summary>How far from the centre a square of half-size
    /// <paramref name="half"/> ends in a direction.</summary>
    internal static float ToEdge(float half, float x, float y) =>
        half / Math.Max(Math.Abs(x), Math.Abs(y));

    public void Render(SKCanvas canvas, SKSize size, float scale, in VisualizerFrame frame)
    {
        var levels = frame.Bands;
        var count = levels.Length;
        var half = Math.Min(size.Width, size.Height) / 2 - Margin;
        if (count == 0 || half <= 0)
            return;

        var cx = size.Width / 2;
        var cy = size.Height / 2;
        // A third of the spacing along the square's edge, where spokes are closest.
        _spoke.StrokeWidth = Math.Max(1.5f, 8 * half / (2 * count) * 0.35f);
        _spoke.Color = frame.Palette.Bar;
        var minLength = (float)VisualizerControl.MinBarHeight;

        for (var side = 0; side < 2; side++)
        {
            for (var i = 0; i < count; i++)
            {
                var (x, y) = Direction(i, count, mirrored: side == 1);
                var from = ToEdge(half, x, y) + Gap + _spoke.StrokeWidth / 2;
                var to = from + Math.Max(minLength, levels[i] * (Extent - _spoke.StrokeWidth));
                canvas.DrawLine(cx + x * from, cy + y * from, cx + x * to, cy + y * to, _spoke);
            }
        }
    }

    public void Dispose() => _spoke.Dispose();
}
