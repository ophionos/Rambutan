using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Rambutan.Audio;

namespace Rambutan.App.Controls;

/// <summary>
/// The OBS overlay's bars standing still, for the style editor's preview
/// when nothing plays or motion is reduced: a fixed, music-like spectrum drawn
/// with the overlay's grouping, gaps and peak marks, so bars, peaks and colours
/// can still be judged. It never animates.
/// </summary>
public sealed class OverlayBarsSample : Control
{
    /// <summary>A plausible spectrum, loud in the bass and falling off: one level per band.</summary>
    internal static readonly float[] SampleLevels =
    [
        0.62f, 0.78f, 0.9f, 0.84f, 0.7f, 0.74f, 0.66f, 0.58f,
        0.63f, 0.55f, 0.49f, 0.52f, 0.46f, 0.41f, 0.44f, 0.38f,
        0.35f, 0.37f, 0.31f, 0.28f, 0.3f, 0.25f, 0.22f, 0.24f,
        0.19f, 0.17f, 0.15f, 0.13f, 0.1f, 0.08f, 0.06f, 0.04f,
    ];

    /// <summary>How far each peak mark sits above its bar.</summary>
    private const float PeakLift = 0.1f;

    private const double MinBarHeight = 2;
    private const double PeakThickness = 2;

    public static readonly StyledProperty<int> BarsProperty =
        AvaloniaProperty.Register<OverlayBarsSample, int>(nameof(Bars), defaultValue: FftAnalyzer.BandCount);

    public static readonly StyledProperty<bool> ShowPeaksProperty =
        AvaloniaProperty.Register<OverlayBarsSample, bool>(nameof(ShowPeaks), defaultValue: true);

    public static readonly StyledProperty<Color> BarColorProperty =
        AvaloniaProperty.Register<OverlayBarsSample, Color>(nameof(BarColor), Colors.White);

    public static readonly StyledProperty<Color> PeakColorProperty =
        AvaloniaProperty.Register<OverlayBarsSample, Color>(nameof(PeakColor), Colors.White);

    static OverlayBarsSample() =>
        AffectsRender<OverlayBarsSample>(BarsProperty, ShowPeaksProperty, BarColorProperty, PeakColorProperty);

    public int Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public bool ShowPeaks
    {
        get => GetValue(ShowPeaksProperty);
        set => SetValue(ShowPeaksProperty, value);
    }

    public Color BarColor
    {
        get => GetValue(BarColorProperty);
        set => SetValue(BarColorProperty, value);
    }

    public Color PeakColor
    {
        get => GetValue(PeakColorProperty);
        set => SetValue(PeakColorProperty, value);
    }

    /// <summary>The bars drawn for <paramref name="count"/>, in the overlay's grouping.</summary>
    internal static float[] Levels(int count)
    {
        var levels = new float[Math.Clamp(count, 1, FftAnalyzer.BandCount)];
        VisualizerControl.GroupOverlayBands(SampleLevels, levels);
        return levels;
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;

        var levels = Levels(Bars);
        var count = levels.Length;
        var gap = VisualizerControl.OverlayGap(size.Width, count);
        var barWidth = (size.Width - (count - 1) * gap) / count;
        if (barWidth <= 0)
            return;

        var radius = Math.Min(barWidth / 2, 2);
        var bar = new SolidColorBrush(BarColor);
        var peak = new SolidColorBrush(PeakColor);
        for (var i = 0; i < count; i++)
        {
            var x = i * (barWidth + gap);
            var height = Math.Max(MinBarHeight, levels[i] * size.Height);
            context.DrawRectangle(bar, null, new RoundedRect(new Rect(x, size.Height - height, barWidth, height), radius));
            if (!ShowPeaks)
                continue;
            var peakY = size.Height - Math.Max(MinBarHeight, Math.Min(1, levels[i] + PeakLift) * size.Height);
            if (peakY < size.Height - height - PeakThickness)
                context.DrawRectangle(peak, null, new RoundedRect(new Rect(x, peakY, barWidth, PeakThickness), radius));
        }
    }
}
