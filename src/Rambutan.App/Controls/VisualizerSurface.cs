using System;
using SkiaSharp;

namespace Rambutan.App.Controls;

/// <summary>
/// An offscreen surface for a style that draws below the screen's resolution
/// and scales up: on the GPU when the frame has a context, in memory when it
/// hasn't. Render thread only, like the renderer that owns it.
/// </summary>
internal sealed class VisualizerSurface : IDisposable
{
    private SKSurface? _surface;
    private GRContext? _context;
    private SKSizeI _size;

    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear);

    /// <summary>The surface at <paramref name="size"/> pixels on
    /// <paramref name="context"/>'s device. A new size or context (a DPI change,
    /// a lost device) gives a new, empty surface; check <paramref name="created"/>.
    /// Null when Skia can't make one.</summary>
    public SKSurface? Ensure(GRContext? context, SKSizeI size, out bool created)
    {
        created = false;
        if (_surface is not null && size == _size && ReferenceEquals(context, _context))
            return _surface;

        _surface?.Dispose();
        _size = size;
        _context = context;
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        _surface = context is null ? SKSurface.Create(info) : SKSurface.Create(context, false, info);
        _surface?.Canvas.Clear(SKColors.Transparent);
        created = _surface is not null;
        return _surface;
    }

    /// <summary>Draws <paramref name="surface"/> stretched over
    /// <paramref name="destination"/>, smoothly: <c>SKSurface.Draw</c> samples
    /// the nearest pixel, which shows the reduced resolution as blocks.
    /// Allocates the snapshot's wrapper (about 100 bytes).</summary>
    public static void Present(SKSurface surface, SKCanvas canvas, SKRect destination, SKPaint paint)
    {
        using var image = surface.Snapshot();
        canvas.DrawImage(image, destination, Linear, paint);
    }

    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
    }
}

/// <summary>
/// How many pixels a full-area style draws: a fragment costs the same whatever
/// it shows, so the area, not the style, sets the price. At most half the
/// screen's resolution, and within a pixel budget, which is far smaller where
/// Skia draws in software, on the CPU.
/// </summary>
internal static class VisualizerRenderScale
{
    /// <summary>The most of the screen's resolution a style draws at.</summary>
    public const float Max = 0.5f;

    /// <summary>The most pixels on the GPU: 1280 × 720.</summary>
    public const int GpuBudget = 1280 * 720;

    /// <summary>The most pixels in software: 320 × 180.</summary>
    public const int SoftwareBudget = 320 * 180;

    /// <summary>The surface size for a <paramref name="size"/> DIP area at
    /// <paramref name="scale"/> device pixels per DIP; at least 1 × 1.</summary>
    public static SKSizeI Pixels(SKSize size, float scale, bool gpu)
    {
        var width = Math.Max(1, size.Width * scale);
        var height = Math.Max(1, size.Height * scale);
        var budget = gpu ? GpuBudget : SoftwareBudget;
        var factor = MathF.Min(Max, MathF.Sqrt(budget / (width * height)));
        return new SKSizeI(
            Math.Max(1, (int)MathF.Ceiling(width * factor)),
            Math.Max(1, (int)MathF.Ceiling(height * factor)));
    }
}

/// <summary>
/// The audio as an animated style reads it: the bands summed into lows, mids
/// and highs, and everything eased, so a style's brightness can't follow a
/// level that jumps from frame to frame. Easing is what keeps the presets
/// from flashing (WCAG 2.3.1): a pulse needs about a tenth of a second to
/// rise and longer to fall.
/// </summary>
internal sealed class EasedAudio
{
    private const double RiseTau = 0.08;
    private const double FallTau = 0.25;

    private double _lastTime = double.NaN;

    public float Bass { get; private set; }
    public float Mid { get; private set; }
    public float High { get; private set; }
    public float Beat { get; private set; }

    /// <summary>The seconds since the last frame, 0 on the first, at most 0.1.</summary>
    public float Delta { get; private set; }

    public void Update(in VisualizerFrame frame)
    {
        Delta = double.IsNaN(_lastTime) ? 0 : (float)Math.Clamp(frame.Time - _lastTime, 0, 0.1);
        _lastTime = frame.Time;

        var bands = frame.Bands;
        var lows = bands.Length / 8;
        var mids = bands.Length / 2;
        Bass = Ease(Bass, Mean(bands[..lows]));
        Mid = Ease(Mid, Mean(bands[lows..mids]));
        High = Ease(High, Mean(bands[mids..]));
        Beat = Ease(Beat, frame.Beat);
    }

    private float Ease(float current, float target)
    {
        var tau = target > current ? RiseTau : FallTau;
        return current + (target - current) * (float)(1 - Math.Exp(-Delta / tau));
    }

    private static float Mean(ReadOnlySpan<float> values)
    {
        if (values.IsEmpty)
            return 0;
        float sum = 0;
        foreach (var value in values)
            sum += value;
        return Math.Clamp(sum / values.Length, 0, 1);
    }
}
