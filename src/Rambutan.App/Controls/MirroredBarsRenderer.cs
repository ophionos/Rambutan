using System;
using SkiaSharp;

namespace Rambutan.App.Controls;

/// <summary>Spectrum bars grown both ways from the middle line, with peak marks
/// above and below.</summary>
internal sealed class MirroredBarsRenderer : IVisualizerRenderer
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

        var middle = size.Height / 2;
        var radius = Math.Min(barWidth / 2, 2f);
        for (var i = 0; i < count; i++)
        {
            var x = i * (barWidth + gap);
            var height = Math.Max(minHeight, levels[i] * size.Height);
            _paint.Color = frame.Palette.Bar;
            canvas.DrawRoundRect(x, middle - height / 2, barWidth, height, radius, radius, _paint);

            if (peaks.Length == 0)
                continue;
            var reach = Math.Max(minHeight, peaks[i] * size.Height) / 2;
            if (reach > height / 2 + peakThickness)
            {
                _paint.Color = frame.Palette.Peak;
                canvas.DrawRoundRect(x, middle - reach, barWidth, peakThickness, radius, radius, _paint);
                canvas.DrawRoundRect(x, middle + reach - peakThickness, barWidth, peakThickness, radius, radius, _paint);
            }
        }
    }

    public void Dispose() => _paint.Dispose();
}
