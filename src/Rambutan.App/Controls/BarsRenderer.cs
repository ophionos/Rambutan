using System;
using SkiaSharp;

namespace Rambutan.App.Controls;

/// <summary>Spectrum bars with peak marks, grown from the bottom: the Now Playing
/// band's style.</summary>
internal sealed class BarsRenderer : IVisualizerRenderer
{
    private readonly SKPaint _paint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    public VisualizerInputs Inputs => VisualizerInputs.None;

    public bool IsAnimated => false;

    public void Render(SKCanvas canvas, SKSize size, float scale, in VisualizerFrame frame)
    {
        var levels = frame.Bands;
        var peaks = frame.Peaks;
        var count = levels.Length;
        var gap = (float)VisualizerControl.BarGap;
        var minHeight = (float)VisualizerControl.MinBarHeight;
        var peakThickness = (float)VisualizerControl.PeakThickness;
        var barWidth = (size.Width - (count - 1) * gap) / count;
        if (barWidth <= 0)
            return;

        var radius = Math.Min(barWidth / 2, 2f);
        for (var i = 0; i < count; i++)
        {
            var x = i * (barWidth + gap);
            var height = Math.Max(minHeight, levels[i] * size.Height);
            _paint.Color = frame.Palette.Bar;
            canvas.DrawRoundRect(x, size.Height - height, barWidth, height, radius, radius, _paint);

            if (peaks.Length == 0)
                continue;
            var peakY = size.Height - Math.Max(minHeight, peaks[i] * size.Height);
            if (peakY < size.Height - height - peakThickness)
            {
                _paint.Color = frame.Palette.Peak;
                canvas.DrawRoundRect(x, peakY, barWidth, peakThickness, radius, radius, _paint);
            }
        }
    }

    public void Dispose() => _paint.Dispose();
}
