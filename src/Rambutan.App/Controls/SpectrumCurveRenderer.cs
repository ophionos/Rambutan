using System;
using SkiaSharp;

namespace Rambutan.App.Controls;

/// <summary>
/// The spectrum as a smooth curve through the band levels: a line, or the
/// line over an area filled with a gradient that fades towards the bottom.
/// </summary>
internal sealed class SpectrumCurveRenderer(bool filled) : IVisualizerRenderer
{
    private const float LineWidth = 2;

    private readonly SKPath _line = new();
    private readonly SKPath _area = new();
    private readonly SKPaint _stroke = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = LineWidth,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
    };
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    // The gradient depends on the height and colour alone: rebuilt when they
    // change, not every frame.
    private float _shaderHeight;
    private SKColor _shaderColor;

    public VisualizerInputs Inputs => VisualizerInputs.None;

    public bool IsAnimated => false;

    public void Render(SKCanvas canvas, SKSize size, float scale, in VisualizerFrame frame)
    {
        var levels = frame.Bands;
        if (levels.Length < 2 || size.Width <= 0)
            return;

        // Half the line's width in from the edges, so a level of 0 or 1 isn't
        // cut in half by the bounds.
        var top = LineWidth / 2;
        var bottom = size.Height - LineWidth / 2;
        Trace(_line, levels, size.Width, top, bottom, moveFirst: true);

        if (filled)
        {
            if (_shaderHeight != size.Height || _shaderColor != frame.Palette.Bar)
            {
                _shaderHeight = size.Height;
                _shaderColor = frame.Palette.Bar;
                _fill.Shader?.Dispose();
                _fill.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0), new SKPoint(0, size.Height),
                    [_shaderColor.WithAlpha(0x8C), _shaderColor.WithAlpha(0x10)],
                    SKShaderTileMode.Clamp);
            }
            _area.Reset();
            _area.MoveTo(0, size.Height);
            Trace(_area, levels, size.Width, top, bottom, moveFirst: false);
            _area.LineTo(size.Width, size.Height);
            _area.Close();
            canvas.DrawPath(_area, _fill);
        }

        _stroke.Color = frame.Palette.Bar;
        canvas.DrawPath(_line, _stroke);
    }

    /// <summary>
    /// Adds a Catmull-Rom curve through the levels, spread evenly from 0 to
    /// <paramref name="width"/>, to <paramref name="path"/>. Control points are
    /// clamped to the band, so the curve never overshoots its top or bottom.
    /// </summary>
    internal static void Trace(SKPath path, ReadOnlySpan<float> levels, float width, float top, float bottom, bool moveFirst)
    {
        if (moveFirst)
            path.Reset();
        var last = levels.Length - 1;
        var step = width / last;
        var start = Point(levels, 0, step, top, bottom);
        if (moveFirst)
            path.MoveTo(start);
        else
            path.LineTo(start);

        for (var i = 0; i < last; i++)
        {
            var p0 = Point(levels, Math.Max(i - 1, 0), step, top, bottom);
            var p1 = Point(levels, i, step, top, bottom);
            var p2 = Point(levels, i + 1, step, top, bottom);
            var p3 = Point(levels, Math.Min(i + 2, last), step, top, bottom);
            var c1 = new SKPoint(p1.X + (p2.X - p0.X) / 6, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, top, bottom));
            var c2 = new SKPoint(p2.X - (p3.X - p1.X) / 6, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, top, bottom));
            path.CubicTo(c1, c2, p2);
        }
    }

    private static SKPoint Point(ReadOnlySpan<float> levels, int i, float step, float top, float bottom) =>
        new(i * step, bottom - Math.Clamp(levels[i], 0, 1) * (bottom - top));

    public void Dispose()
    {
        _line.Dispose();
        _area.Dispose();
        _fill.Shader?.Dispose();
        _stroke.Dispose();
        _fill.Dispose();
    }
}
