using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Rambutan.App.ViewModels;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Playback;
using Rambutan.Lyrics.Models;

namespace Rambutan.App.Controls;

/// <summary>
/// One lyric line, swept word by word while it is the active line.
/// The text is laid out once and drawn twice: in
/// <see cref="Foreground"/>, then in <see cref="SungBrush"/> clipped to the sung
/// part of each word. The sung part comes from the text layout's own hit-testing,
/// so it follows wrapped lines and fallback fonts (CJK, emoji) rather than
/// assuming one run of Latin text, and any <see cref="TextAlignment"/>. Words are
/// swept left to right.
/// </summary>
/// <remarks>
/// Only the active line with word timings animates: it reads <see cref="Clock"/>
/// on each frame and invalidates its own render, so the sweep never goes through
/// a binding. An active line without word timings is drawn whole in
/// <see cref="SungBrush"/> (line-level highlighting); inactive lines in
/// <see cref="Foreground"/>. Under reduced motion each word lights whole at its
/// start, with no moving edge.
/// </remarks>
public sealed class KaraokeControl : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<KaraokeControl, string?>(nameof(Text));

    public static readonly StyledProperty<IReadOnlyList<KaraokeSpan>?> WordsProperty =
        AvaloniaProperty.Register<KaraokeControl, IReadOnlyList<KaraokeSpan>?>(nameof(Words));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<KaraokeControl, bool>(nameof(IsActive));

    public static readonly StyledProperty<PlaybackClock?> ClockProperty =
        AvaloniaProperty.Register<KaraokeControl, PlaybackClock?>(nameof(Clock));

    public static readonly StyledProperty<IBrush?> SungBrushProperty =
        AvaloniaProperty.Register<KaraokeControl, IBrush?>(nameof(SungBrush), Brushes.White);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<KaraokeControl>();

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<KaraokeControl>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<KaraokeControl>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner<KaraokeControl>();

    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        TextBlock.TextAlignmentProperty.AddOwner<KaraokeControl>();

    /// <summary>How wide the sweep's soft edge is, in pixels.</summary>
    public const double Feather = 14;

    // Frame-pacing evidence: opt-in, like RAMBUTAN_VIZ_TRACE.
    private static readonly bool Trace =
        Environment.GetEnvironmentVariable("RAMBUTAN_KARAOKE_TRACE") is not (null or "");

    private TextLayout? _base;
    private TextLayout? _sung;
    private double _layoutWidth = double.NaN;
    private bool _frameRequested;
    private TimeSpan _drawnAt = TimeSpan.MinValue;

    // Shared by every line: a line is active for seconds, the trace window is longer.
    // Only one line sweeps at a time, and all of them run on the UI thread.
    private static readonly Stopwatch _traceWindow = new();
    private static TimeSpan _traceLastFrame;
    private static int _traceFrames;
    private static double _traceWorstGapMs;
    private static double _traceWorstRenderMs;
    private static double _traceSweepMs;

    static KaraokeControl()
    {
        AffectsMeasure<KaraokeControl>(TextProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty,
            TextAlignmentProperty);
        AffectsRender<KaraokeControl>(WordsProperty, IsActiveProperty, ForegroundProperty, SungBrushProperty);
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IReadOnlyList<KaraokeSpan>? Words { get => GetValue(WordsProperty); set => SetValue(WordsProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public PlaybackClock? Clock { get => GetValue(ClockProperty); set => SetValue(ClockProperty, value); }
    public IBrush? SungBrush { get => GetValue(SungBrushProperty); set => SetValue(SungBrushProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public TextAlignment TextAlignment { get => GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }

    private bool Sweeps => IsActive && Words is { Count: > 0 } && Clock is not null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // The brushes and the alignment are baked into the layouts.
        if (change.Property == ForegroundProperty || change.Property == SungBrushProperty ||
            change.Property == TextAlignmentProperty)
            _layoutWidth = double.NaN;

        if (change.Property == IsActiveProperty || change.Property == WordsProperty ||
            change.Property == ClockProperty)
        {
            _drawnAt = TimeSpan.MinValue;
            RequestFrame();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestFrame();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var layout = Layout(availableSize.Width);
        return layout is null ? default : new Size(Math.Min(layout.WidthIncludingTrailingWhitespace, availableSize.Width), layout.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width);
        return finalSize;
    }

    private TextLayout? Layout(double width)
    {
        if (string.IsNullOrEmpty(Text))
        {
            _base = _sung = null;
            return null;
        }

        if (_base is not null && width.Equals(_layoutWidth))
            return _base;

        _base?.Dispose();
        _sung?.Dispose();
        _layoutWidth = width;
        _base = Build(Foreground, width);
        _sung = Build(SungBrush, width);
        return _base;
    }

    private TextLayout Build(IBrush? brush, double width) => new(
        Text,
        new Typeface(FontFamily, FontStyle.Normal, FontWeight),
        FontSize,
        brush,
        TextAlignment,
        TextWrapping.Wrap,
        maxWidth: double.IsFinite(width) ? width : double.PositiveInfinity);

    public override void Render(DrawingContext context)
    {
        if (double.IsNaN(_layoutWidth))
            Layout(Bounds.Width); // a brush changed since the last arrange

        if (_base is null || _sung is null)
            return;

        var started = Trace ? Stopwatch.GetTimestamp() : 0;

        if (!IsActive)
        {
            _base.Draw(context, default);
            return;
        }

        if (!Sweeps)
        {
            _sung.Draw(context, default);
            return;
        }

        var now = Clock!.Now;
        _drawnAt = now;
        var stepped = TopLevel.GetTopLevel(this)?.Classes.Contains("reducedmotion") ?? false;

        _base.Draw(context, default);
        foreach (var span in Words!)
        {
            var progress = LyricTiming.Progress(span.Word, now);
            if (stepped && progress > 0)
                progress = 1;
            if (progress <= 0)
                continue;

            var rects = _base.HitTestTextRange(span.Start, span.Length).ToList();
            var sung = KaraokeGeometry.Sung(rects, progress, stepped ? 0 : Feather);
            foreach (var solid in sung.Solid)
            {
                using (context.PushClip(solid))
                    _sung.Draw(context, default);
            }

            if (sung.Edge is { } edge)
            {
                using (context.PushClip(edge))
                using (context.PushOpacityMask(EdgeMask, edge))
                    _sung.Draw(context, default);
            }
        }

        if (Trace)
            _traceWorstRenderMs = Math.Max(_traceWorstRenderMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Opaque at the sung side, clear at the unsung side of the edge rect.</summary>
    private static readonly IBrush EdgeMask = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Transparent, 1) },
    };

    private void RequestFrame()
    {
        if (_frameRequested || !Sweeps)
            return;

        if (TopLevel.GetTopLevel(this) is not { } top)
            return;

        _frameRequested = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan frameTime)
    {
        _frameRequested = false;
        if (!Sweeps || !IsEffectivelyVisible)
            return;

        // Redraw only when the clock moved: a paused sweep costs a clock read a frame.
        if (Clock!.Now != _drawnAt)
            InvalidateVisual();

        if (Trace)
            TraceFrame(frameTime);

        RequestFrame();
    }

    private static void TraceFrame(TimeSpan frameTime)
    {
        if (!_traceWindow.IsRunning)
        {
            _traceWindow.Start();
            _traceLastFrame = frameTime;
            return;
        }

        // Between sweeping lines no frames are asked for; that pause isn't a slow frame.
        var gap = (frameTime - _traceLastFrame).TotalMilliseconds;
        if (gap < 250)
        {
            _traceFrames++;
            _traceSweepMs += gap;
            _traceWorstGapMs = Math.Max(_traceWorstGapMs, gap);
        }
        _traceLastFrame = frameTime;

        if (_traceWindow.Elapsed < TimeSpan.FromSeconds(5))
            return;

        AppLog.Write("karaoke",
            $"{(_traceSweepMs > 0 ? _traceFrames * 1000 / _traceSweepMs : 0):0.0} fps while sweeping, worst frame gap {_traceWorstGapMs:0.0} ms, " +
            $"worst render {_traceWorstRenderMs:0.00} ms");
        _traceWindow.Restart();
        _traceFrames = 0;
        _traceWorstGapMs = 0;
        _traceWorstRenderMs = 0;
        _traceSweepMs = 0;
    }
}

/// <summary>Where a word's sung part lies, given its text rectangles (one per
/// visual line it spans, in reading order) and how far it is sung.</summary>
public static class KaraokeGeometry
{
    /// <summary>The sung part: whole rectangles, plus at most one soft edge.</summary>
    /// <param name="Solid">Drawn fully in the sung colour.</param>
    /// <param name="Edge">The soft edge straddling the sweep's position, fading out
    /// to the right; null when the word is complete or there is no feather.</param>
    public readonly record struct Result(IReadOnlyList<Rect> Solid, Rect? Edge);

    /// <summary>
    /// Splits <paramref name="progress"/> of the word's total width across its
    /// rectangles in order. The edge is centred on the sweep position and kept
    /// inside the rectangle the sweep is in.
    /// </summary>
    public static Result Sung(IReadOnlyList<Rect> rects, double progress, double feather)
    {
        if (rects.Count == 0 || progress <= 0)
            return new([], null);
        if (progress >= 1)
            return new(rects.ToArray(), null);

        var total = rects.Sum(r => r.Width);
        var remaining = total * progress;
        var solid = new List<Rect>();
        foreach (var rect in rects)
        {
            if (remaining >= rect.Width)
            {
                solid.Add(rect);
                remaining -= rect.Width;
                continue;
            }

            // The sweep is inside this rectangle, `remaining` from its left.
            var half = feather / 2;
            var solidWidth = Math.Max(0, remaining - half);
            if (solidWidth > 0)
                solid.Add(new Rect(rect.X, rect.Y, solidWidth, rect.Height));

            Rect? edge = null;
            var edgeRight = Math.Min(rect.Width, remaining + half);
            if (feather > 0 && edgeRight > solidWidth)
                edge = new Rect(rect.X + solidWidth, rect.Y, edgeRight - solidWidth, rect.Height);

            return new(solid, edge);
        }

        return new(solid, null);
    }
}
