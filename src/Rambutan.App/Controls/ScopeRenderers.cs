using System;
using SkiaSharp;

namespace Rambutan.App.Controls;

/// <summary>
/// The waveform as an oscilloscope trace. Each frame starts at a rising zero
/// crossing, so a steady tone stands still instead of sliding sideways.
/// </summary>
internal sealed class OscilloscopeRenderer : IVisualizerRenderer
{
    /// <summary>How many samples a frame shows: half the waveform (about 21 ms),
    /// leaving the other half to find a trigger in.</summary>
    internal const int Window = 256;

    private readonly SKPath _path = new();
    private readonly SKPaint _stroke = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 2,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
    };

    public VisualizerInputs Inputs => VisualizerInputs.Waveform;

    public bool IsAnimated => true;

    /// <summary>The first index where <paramref name="samples"/> crosses zero
    /// going up, within the first <paramref name="limit"/>; 0 when none does.</summary>
    internal static int Trigger(ReadOnlySpan<float> samples, int limit)
    {
        limit = Math.Min(limit, samples.Length - 1);
        for (var i = 1; i <= limit; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
                return i;
        }
        return 0;
    }

    public void Render(SKCanvas canvas, SKSize size, float scale, in VisualizerFrame frame)
    {
        var samples = frame.Waveform;
        if (samples.Length < 2 || size.Width <= 0)
            return;

        var window = Math.Min(Window, samples.Length);
        var start = Trigger(samples, samples.Length - window);
        var middle = size.Height / 2;
        var reach = middle - _stroke.StrokeWidth;
        var step = size.Width / (window - 1);

        _path.Reset();
        for (var i = 0; i < window; i++)
        {
            var y = middle - Math.Clamp(samples[start + i], -1, 1) * reach;
            if (i == 0)
                _path.MoveTo(0, y);
            else
                _path.LineTo(i * step, y);
        }
        _stroke.Color = frame.Palette.Bar;
        canvas.DrawPath(_path, _stroke);
    }

    public void Dispose()
    {
        _path.Dispose();
        _stroke.Dispose();
    }
}

/// <summary>
/// A vectorscope (goniometer): left against right, turned 45° so a mono signal
/// is a vertical line and stereo width spreads it sideways. A square in the
/// middle of the area, over faint axes.
/// </summary>
internal sealed class VectorscopeRenderer : IVisualizerRenderer
{
    /// <summary>Music rarely reaches full scale on both channels at once; this
    /// fills the square with a typical master instead of a small blob.</summary>
    private const float Gain = 1.4f;

    private readonly SKPath _path = new();
    private readonly SKPaint _stroke = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1.5f,
        StrokeJoin = SKStrokeJoin.Round,
    };
    private readonly SKPaint _axes = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };

    public VisualizerInputs Inputs => VisualizerInputs.Waveform;

    public bool IsAnimated => true;

    /// <summary>Where a left/right sample pair lands, -1..1 on each axis
    /// (y up): mid up the middle, side across.</summary>
    internal static (float X, float Y) Plot(float left, float right) =>
        (Math.Clamp((left - right) * 0.5f * Gain, -1, 1), Math.Clamp((left + right) * 0.5f * Gain, -1, 1));

    public void Render(SKCanvas canvas, SKSize size, float scale, in VisualizerFrame frame)
    {
        var left = frame.Left;
        var right = frame.Right;
        var side = Math.Min(size.Width, size.Height);
        if (left.Length < 2 || side <= 0)
            return;

        var cx = size.Width / 2;
        var cy = size.Height / 2;
        var half = side / 2 - _stroke.StrokeWidth;

        _axes.StrokeWidth = 1 / scale;
        _axes.Color = frame.Palette.Bar.WithAlpha(0x30);
        canvas.DrawLine(cx, cy - half, cx, cy + half, _axes);
        canvas.DrawLine(cx - half, cy, cx + half, cy, _axes);

        _path.Reset();
        for (var i = 0; i < left.Length; i++)
        {
            var (x, y) = Plot(left[i], right[i]);
            if (i == 0)
                _path.MoveTo(cx + x * half, cy - y * half);
            else
                _path.LineTo(cx + x * half, cy - y * half);
        }
        _stroke.Color = frame.Palette.Bar.WithAlpha(0xC0);
        canvas.DrawPath(_path, _stroke);
    }

    public void Dispose()
    {
        _path.Dispose();
        _stroke.Dispose();
        _axes.Dispose();
    }
}
