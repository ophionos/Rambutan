using System;
using SkiaSharp;

namespace Rambutan.App.Controls;

/// <summary>Bars as stacks of LED segments, Winamp-style: lit segments to the
/// level, the rest dimly lit, and the peak as one segment that falls.</summary>
internal sealed class LedRenderer : IVisualizerRenderer
{
    /// <summary>A segment's height and the gap above it, in DIPs.</summary>
    internal const float SegmentHeight = 4;
    internal const float SegmentGap = 2;

    /// <summary>How much of the bar colour an unlit segment shows.</summary>
    private const byte UnlitAlpha = 0x24;

    private readonly SKPaint _lit = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _unlit = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _peak = new() { IsAntialias = false, Style = SKPaintStyle.Fill };

    public VisualizerInputs Inputs => VisualizerInputs.None;

    public bool IsAnimated => false;

    /// <summary>How many segments fit in <paramref name="height"/>.</summary>
    internal static int Segments(float height) =>
        Math.Max(1, (int)((height + SegmentGap) / (SegmentHeight + SegmentGap)));

    /// <summary>Lit segments for a 0..1 level: rounded, so a bar lights its next
    /// segment once it is half way there.</summary>
    internal static int Lit(float level, int segments) =>
        Math.Clamp((int)MathF.Round(level * segments), 0, segments);

    public void Render(SKCanvas canvas, SKSize size, float scale, in VisualizerFrame frame)
    {
        var levels = frame.Bands;
        var peaks = frame.Peaks;
        var count = levels.Length;
        var gap = (float)VisualizerControl.BarGap;
        var barWidth = (size.Width - (count - 1) * gap) / count;
        if (barWidth <= 0)
            return;

        _lit.Color = frame.Palette.Bar;
        _unlit.Color = frame.Palette.Bar.WithAlpha(UnlitAlpha);
        _peak.Color = frame.Palette.Peak;
        var segments = Segments(size.Height);
        var pitch = SegmentHeight + SegmentGap;
        // Segments sit on whole device pixels, so their gaps don't shimmer as
        // levels move.
        for (var i = 0; i < count; i++)
        {
            var x = Snap(i * (barWidth + gap), scale);
            var width = Snap(i * (barWidth + gap) + barWidth, scale) - x;
            var lit = Lit(levels[i], segments);
            var peak = peaks.Length > 0 ? Lit(peaks[i], segments) - 1 : -1;
            for (var s = 0; s < segments; s++)
            {
                var y = Snap(size.Height - s * pitch - SegmentHeight, scale);
                var paint = s < lit ? _lit : s == peak ? _peak : _unlit;
                canvas.DrawRect(x, y, width, Snap(y + SegmentHeight, scale) - y, paint);
            }
        }
    }

    private static float Snap(float value, float scale) => MathF.Round(value * scale) / scale;

    public void Dispose()
    {
        _lit.Dispose();
        _unlit.Dispose();
        _peak.Dispose();
    }
}
