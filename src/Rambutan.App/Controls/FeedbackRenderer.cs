using System;
using SkiaSharp;

namespace Rambutan.App.Controls;

/// <summary>
/// A MilkDrop-style feedback loop: each frame draws the last one zoomed,
/// turned and faded, then a ring of the spectrum on top, so the rings drift
/// outward as trails. Two surfaces at <see cref="VisualizerRenderScale"/>,
/// swapped each frame. The motion follows the activity and the fade follows
/// the clock, so when the feed stops the trails fade out around a still ring.
/// </summary>
internal sealed class FeedbackRenderer : IVisualizerRenderer
{
    /// <summary>How fast the trails fade: to a third in this many seconds.</summary>
    internal const double FadeTau = 0.35;

    private readonly VisualizerSurface _first = new();
    private readonly VisualizerSurface _second = new();
    private bool _firstIsFront = true;
    private readonly EasedAudio _audio = new();
    private readonly SKPath _ring = new();
    private readonly SKPaint _fade = new();
    private readonly SKPaint _stroke = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeJoin = SKStrokeJoin.Round,
    };
    private readonly SKPaint _present = new();
    private float _turn;

    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear);

    public VisualizerInputs Inputs => VisualizerInputs.Beat;

    public bool IsAnimated => true;

    /// <summary>How much of the last frame stays after <paramref name="seconds"/>.</summary>
    internal static float Keep(float seconds) => (float)Math.Exp(-seconds / FadeTau);

    /// <summary>How much the last frame grows in <paramref name="seconds"/>: a
    /// slow drift, pushed by the bass and the beat, none without activity.</summary>
    internal static float Zoom(float seconds, float activity, float bass, float beat) =>
        1 + seconds * activity * (0.25f + 0.5f * bass + 0.6f * beat);

    public void Render(SKCanvas canvas, SKSize size, float scale, in VisualizerFrame frame)
    {
        _audio.Update(in frame);
        var dt = _audio.Delta;
        var pixels = VisualizerRenderScale.Pixels(size, scale, frame.Gpu is not null);
        var front = (_firstIsFront ? _first : _second).Ensure(frame.Gpu, pixels, out var newFront);
        var back = (_firstIsFront ? _second : _first).Ensure(frame.Gpu, pixels, out _);
        if (front is null || back is null)
            return;
        if (newFront)
            dt = 0;

        var w = pixels.Width;
        var h = pixels.Height;
        var cx = w / 2f;
        var cy = h / 2f;
        var unit = Math.Min(w, h);

        // The last frame, zoomed about the centre, turned, faded.
        var target = back.Canvas;
        target.Clear(SKColors.Transparent);
        var zoom = Zoom(dt, frame.Activity, _audio.Bass, _audio.Beat);
        var turn = dt * frame.Activity * (6 + 20 * _audio.Mid);
        _fade.Color = new SKColor(0, 0, 0, (byte)Math.Round(255 * Keep(dt)));
        var saved = target.Save();
        target.Translate(cx, cy);
        target.Scale(zoom);
        target.RotateDegrees(turn);
        target.Translate(-cx, -cy);
        using (var last = front.Snapshot())
            target.DrawImage(last, 0, 0, Linear, _fade);
        target.RestoreToCount(saved);

        // This frame's ring: the spectrum around a circle, mirrored left and
        // right, the lows at the bottom; it turns slowly on its own.
        _turn = (_turn + dt * frame.Activity * 0.3f) % (2 * MathF.PI);
        TraceRing(_ring, frame.Bands, cx, cy, unit * 0.16f, unit * 0.22f, _turn);
        // In a wide area such as the band, a circle sized to the height leaves
        // most of the width empty; the ring widens to fill more of it.
        var stretch = Math.Clamp(w / (float)h / 1.6f, 1f, 2.5f);
        if (stretch > 1)
            _ring.Transform(SKMatrix.CreateScale(stretch, 1, cx, cy));
        _stroke.Color = frame.Palette.Bar;
        // 2.5 DIPs, in the surface's pixels.
        _stroke.StrokeWidth = Math.Max(1, 2.5f * w / Math.Max(1, size.Width));
        target.DrawPath(_ring, _stroke);

        _firstIsFront = !_firstIsFront;
        VisualizerSurface.Present(back, canvas, new SKRect(0, 0, size.Width, size.Height), _present);
    }

    /// <summary>The spectrum as a closed ring of radius <paramref name="radius"/>
    /// plus up to <paramref name="reach"/> per band, starting at the bottom and
    /// climbing both sides; <paramref name="turn"/> in radians.</summary>
    internal static void TraceRing(SKPath path, ReadOnlySpan<float> bands, float cx, float cy, float radius, float reach, float turn)
    {
        path.Rewind();
        var count = bands.Length;
        if (count == 0)
            return;
        var points = count * 2;
        for (var i = 0; i < points; i++)
        {
            // Up the right side, then back down the left.
            var band = i < count ? i : points - 1 - i;
            var angle = (i + 0.5f) / points * 2 * MathF.PI + turn;
            var r = radius + Math.Clamp(bands[band], 0, 1) * reach;
            var x = cx + MathF.Sin(angle) * r;
            var y = cy + MathF.Cos(angle) * r;
            if (i == 0)
                path.MoveTo(x, y);
            else
                path.LineTo(x, y);
        }
        path.Close();
    }

    public void Dispose()
    {
        _first.Dispose();
        _second.Dispose();
        _ring.Dispose();
        _fade.Dispose();
        _stroke.Dispose();
        _present.Dispose();
    }
}
