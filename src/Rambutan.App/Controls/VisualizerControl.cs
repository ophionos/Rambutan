using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Rambutan.Core.Diagnostics;
using Rambutan.Audio;

namespace Rambutan.App.Controls;

/// <summary>
/// Spectrum bars fed from <see cref="FftAnalyzer"/>: a
/// CompositionCustomVisual polls the latest frame per animation frame and draws
/// on the render thread — no UI-thread work, no per-frame allocation. Smoothing
/// (attack/decay envelopes, peak-hold) lives here, not in the analyzer, which
/// keeps emitting raw bands for other consumers (overlay).
/// With <see cref="Groups"/> set it draws a compact meter instead: a few thin bars
/// without peaks, grown up and down from the middle, for small places like the
/// player bar's artwork.
/// </summary>
public sealed class VisualizerControl : Control
{
    /// <summary>Analyzer accessor, set once at composition time (the engine —
    /// and with it the analyzer — is created lazily on first play).</summary>
    public static Func<FftAnalyzer?>? Source { get; set; }

    /// <summary>Polling and drawing run only while active — bind to the hosting
    /// surface's visibility so a hidden visualizer costs nothing.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<VisualizerControl, bool>(nameof(IsActive));

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>0 draws every band with peak caps; above 0, that many grouped
    /// bars without peaks.</summary>
    public static readonly StyledProperty<int> GroupsProperty =
        AvaloniaProperty.Register<VisualizerControl, int>(nameof(Groups));

    public int Groups
    {
        get => GetValue(GroupsProperty);
        set => SetValue(GroupsProperty, value);
    }

    /// <summary>Bar colour; the accent when unset.</summary>
    public static readonly StyledProperty<Color?> BarColorProperty =
        AvaloniaProperty.Register<VisualizerControl, Color?>(nameof(BarColor));

    public Color? BarColor
    {
        get => GetValue(BarColorProperty);
        set => SetValue(BarColorProperty, value);
    }

    /// <summary>Above 0, the OBS overlay's bars instead (the style editor's
    /// preview): that many full-height bars, grouped as the overlay page groups
    /// them, with its gaps. 0 draws every band. Separate from <see cref="Groups"/>,
    /// whose compact meter has no peaks and grows from the middle.</summary>
    public static readonly StyledProperty<int> OverlayBarsProperty =
        AvaloniaProperty.Register<VisualizerControl, int>(nameof(OverlayBars));

    public int OverlayBars
    {
        get => GetValue(OverlayBarsProperty);
        set => SetValue(OverlayBarsProperty, value);
    }

    /// <summary>The thin peak marks over full-height bars.</summary>
    public static readonly StyledProperty<bool> ShowPeaksProperty =
        AvaloniaProperty.Register<VisualizerControl, bool>(nameof(ShowPeaks), defaultValue: true);

    public bool ShowPeaks
    {
        get => GetValue(ShowPeaksProperty);
        set => SetValue(ShowPeaksProperty, value);
    }

    /// <summary>Above 0, redraws at most this many times a second; 0 follows the
    /// display. Each redraw makes the compositor present a frame, and that frame
    /// is the visualizer's GPU cost.</summary>
    public static readonly StyledProperty<int> MaxFrameRateProperty =
        AvaloniaProperty.Register<VisualizerControl, int>(nameof(MaxFrameRate));

    public int MaxFrameRate
    {
        get => GetValue(MaxFrameRateProperty);
        set => SetValue(MaxFrameRateProperty, value);
    }

    /// <summary>Peak colour; translucent white when unset.</summary>
    public static readonly StyledProperty<Color?> PeakColorProperty =
        AvaloniaProperty.Register<VisualizerControl, Color?>(nameof(PeakColor));

    public Color? PeakColor
    {
        get => GetValue(PeakColorProperty);
        set => SetValue(PeakColorProperty, value);
    }

    /// <summary>
    /// Folds the bands into the overlay's bars the way its page does
    /// (web/overlay Visualizer.svelte): as many bars as bands means every band,
    /// the top quarter included; fewer go through <see cref="GroupBands"/>.
    /// </summary>
    internal static void GroupOverlayBands(ReadOnlySpan<float> bands, Span<float> bars)
    {
        if (bars.Length >= bands.Length)
            bands.CopyTo(bars);
        else
            GroupBands(bands, bars);
    }

    /// <summary>The overlay page's gap between <paramref name="count"/> bars across
    /// <paramref name="width"/>: a quarter of a slot, 2 to 4 pixels.</summary>
    internal static double OverlayGap(double width, int count) => Math.Clamp(width / count / 4, 2, 4);

    /// <summary>
    /// Folds the analyzer's bands into <paramref name="groups"/>.Length bars, each
    /// the loudest band in its slice. The top quarter of the spectrum is left out:
    /// it is near silent in most music, so it would only add bars at rest.
    /// </summary>
    internal static void GroupBands(ReadOnlySpan<float> bands, Span<float> groups)
    {
        var used = bands.Length * 3 / 4;
        for (var g = 0; g < groups.Length; g++)
        {
            var start = g * used / groups.Length;
            var end = Math.Max(start + 1, (g + 1) * used / groups.Length);
            float level = 0;
            for (var i = start; i < end && i < used; i++)
                level = Math.Max(level, bands[i]);
            groups[g] = level;
        }
    }

    /// <summary>
    /// Where compact bar <paramref name="index"/> of <paramref name="count"/> goes:
    /// grown from the vertical middle, so the meter stays centred at every level.
    /// Empty when the bars don't fit.
    /// </summary>
    internal static Rect CompactBar(int index, int count, float level, Size size, double gap, double minHeight)
    {
        var barWidth = (size.Width - (count - 1) * gap) / count;
        if (barWidth <= 0)
            return default;
        var height = Math.Max(minHeight, level * size.Height);
        return new Rect(index * (barWidth + gap), (size.Height - height) / 2, barWidth, height);
    }

    private CompositionCustomVisual? _visual;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ElementComposition.GetElementVisual(this) is not { } elementVisual)
            return;

        _visual = elementVisual.Compositor.CreateCustomVisual(new BarsHandler());
        ElementComposition.SetElementChildVisual(this, _visual);
        _visual.Size = new(Bounds.Width, Bounds.Height);
        _visual.SendHandlerMessage(CurrentPalette());
        _visual.SendHandlerMessage(new BarsHandler.Compact(Groups));
        _visual.SendHandlerMessage(new BarsHandler.Overlay(OverlayBars, ShowPeaks));
        _visual.SendHandlerMessage(new BarsHandler.Pacing(MaxFrameRate));
        if (IsActive)
            _visual.SendHandlerMessage(BarsHandler.StartMessage);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _visual?.SendHandlerMessage(BarsHandler.StopMessage);
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _visual is not null)
        {
            var bounds = change.GetNewValue<Rect>();
            _visual.Size = new(bounds.Width, bounds.Height);
        }
        else if (change.Property == IsActiveProperty)
        {
            _visual?.SendHandlerMessage(
                change.GetNewValue<bool>() ? BarsHandler.StartMessage : BarsHandler.StopMessage);
        }
        else if (change.Property == GroupsProperty)
        {
            _visual?.SendHandlerMessage(new BarsHandler.Compact(Groups));
        }
        else if (change.Property == BarColorProperty || change.Property == PeakColorProperty)
        {
            _visual?.SendHandlerMessage(CurrentPalette());
        }
        else if (change.Property == OverlayBarsProperty || change.Property == ShowPeaksProperty)
        {
            _visual?.SendHandlerMessage(new BarsHandler.Overlay(OverlayBars, ShowPeaks));
        }
        else if (change.Property == MaxFrameRateProperty)
        {
            _visual?.SendHandlerMessage(new BarsHandler.Pacing(MaxFrameRate));
        }
    }

    private BarsHandler.Palette CurrentPalette() => new(BarColor ?? ResolveAccent(), PeakColor);

    private Color ResolveAccent() =>
        this.TryFindResource("Token.Color.Accent", out var value) && value is Color color
            ? color
            : Color.FromRgb(0xFF, 0x4E, 0x45);

    private sealed class BarsHandler : CompositionCustomVisualHandler
    {
        public static readonly object StartMessage = new();
        public static readonly object StopMessage = new();
        public sealed record Palette(Color Accent, Color? Peak);
        public sealed record Compact(int Groups);
        public sealed record Overlay(int Bars, bool Peaks);
        public sealed record Pacing(int MaxFrameRate);

        private const double BarGap = 3;
        private const double CompactBarGap = 3;
        private const double MinBarHeight = 2;
        private const double PeakThickness = 2;

        // Rise fast enough to feel live, fall slow enough to read as motion.
        private const double AttackTau = 0.035;
        private const double DecayTau = 0.16;
        private const double PeakFallPerSecond = 0.9;

        /// <summary>No new analyzer frame for this long → feed stopped, decay to rest.</summary>
        private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(0.3);

        private readonly float[] _target = new float[FftAnalyzer.BandCount];
        private readonly float[] _display = new float[FftAnalyzer.BandCount];
        private readonly float[] _peaks = new float[FftAnalyzer.BandCount];
        private float[] _grouped = [];
        private float[] _overlayLevels = [];
        private float[] _overlayPeaks = [];
        private bool _showPeaks = true;
        private readonly VisualizerRedrawGate _gate = new();
        private Vector _gateSize;

        private ImmutableSolidColorBrush _barBrush = new(Color.FromRgb(0xFF, 0x4E, 0x45));
        private ImmutableSolidColorBrush _peakBrush = new(Colors.White, 0.55);
        private bool _running;
        private long _version = -1;
        private TimeSpan _versionChangedAt;
        private TimeSpan _lastFrameAt;

        // Frame-pacing evidence: opt-in via RAMBUTAN_VIZ_TRACE.
        private static readonly bool Trace =
            Environment.GetEnvironmentVariable("RAMBUTAN_VIZ_TRACE") is not (null or "");
        private double _traceWorstMs;
        private int _traceFrames;
        private int _traceDraws;
        private TimeSpan _traceStartedAt;

        public override void OnMessage(object message)
        {
            if (ReferenceEquals(message, StartMessage) && !_running)
            {
                _running = true;
                _lastFrameAt = CompositionNow;
                _versionChangedAt = CompositionNow;
                _traceFrames = 0;
                _traceDraws = 0;
                _traceWorstMs = 0;
                _gate.Reset();
                RegisterForNextAnimationFrameUpdate();
            }
            else if (ReferenceEquals(message, StopMessage))
            {
                _running = false;
            }
            else if (message is Palette palette)
            {
                _barBrush = new ImmutableSolidColorBrush(palette.Accent);
                _peakBrush = palette.Peak is { } peak
                    ? new ImmutableSolidColorBrush(peak)
                    : new ImmutableSolidColorBrush(Colors.White, 0.55);
                _gate.Reset();
                Invalidate();
            }
            else if (message is Overlay overlay)
            {
                var bars = Math.Clamp(overlay.Bars, 0, FftAnalyzer.BandCount);
                _overlayLevels = new float[bars];
                _overlayPeaks = new float[bars];
                _showPeaks = overlay.Peaks;
                _gate.Reset();
                Invalidate();
            }
            else if (message is Compact compact)
            {
                _grouped = new float[Math.Max(0, compact.Groups)];
                _gate.Reset();
                Invalidate();
            }
            else if (message is Pacing pacing)
            {
                _gate.MinInterval = pacing.MaxFrameRate > 0
                    ? TimeSpan.FromSeconds(1.0 / pacing.MaxFrameRate)
                    : TimeSpan.Zero;
            }
        }

        public override void OnAnimationFrameUpdate()
        {
            if (!_running)
                return;

            var now = CompositionNow;
            var dt = Math.Clamp((now - _lastFrameAt).TotalSeconds, 0.001, 0.1);
            _lastFrameAt = now;

            if (Source?.Invoke() is { } analyzer)
            {
                var version = analyzer.CopyLatest(_target);
                if (version != _version)
                {
                    _version = version;
                    _versionChangedAt = now;
                }
            }

            var stale = now - _versionChangedAt > StaleAfter;
            var attack = 1 - Math.Exp(-dt / AttackTau);
            var decay = 1 - Math.Exp(-dt / DecayTau);

            for (var i = 0; i < _display.Length; i++)
            {
                var target = stale ? 0f : _target[i];
                var coefficient = target > _display[i] ? attack : decay;
                _display[i] += (float)((target - _display[i]) * coefficient);
                _peaks[i] = Math.Max(_display[i], _peaks[i] - (float)(PeakFallPerSecond * dt));
            }

            var size = EffectiveSize;
            if (size != _gateSize)
            {
                _gateSize = size;
                _gate.Reset();
            }

            var draw = false;
            if (size.X > 0 && size.Y > 0)
            {
                var (levels, peaks) = Bars();
                draw = _gate.ShouldDraw(now, levels, peaks, size.Y);
            }
            if (draw)
                Invalidate();

            if (Trace)
                RecordFrame(now, dt, draw);

            RegisterForNextAnimationFrameUpdate();
        }

        /// <summary>The levels and peaks as drawn: grouped for the compact meter or
        /// the overlay's bars, and no peaks where none are drawn.</summary>
        private (float[] Levels, float[] Peaks) Bars()
        {
            if (_grouped.Length > 0)
            {
                GroupBands(_display, _grouped);
                return (_grouped, []);
            }
            if (_overlayLevels.Length > 0)
            {
                GroupOverlayBands(_display, _overlayLevels);
                GroupOverlayBands(_peaks, _overlayPeaks);
                return (_overlayLevels, _showPeaks ? _overlayPeaks : []);
            }
            return (_display, _showPeaks ? _peaks : []);
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            var size = EffectiveSize;
            if (size.X <= 0 || size.Y <= 0)
                return;

            var (levels, peaks) = Bars();
            if (_grouped.Length > 0)
            {
                RenderCompact(context, size, levels);
                return;
            }

            var gap = _overlayLevels.Length > 0 ? OverlayGap(size.X, levels.Length) : BarGap;

            var count = levels.Length;
            var barWidth = (size.X - (count - 1) * gap) / count;
            if (barWidth <= 0)
                return;

            var radius = Math.Min(barWidth / 2, 2);
            for (var i = 0; i < count; i++)
            {
                var x = i * (barWidth + gap);
                var height = Math.Max(MinBarHeight, levels[i] * size.Y);
                context.DrawRectangle(
                    _barBrush, null,
                    new Rect(x, size.Y - height, barWidth, height), radius, radius);

                if (peaks.Length == 0)
                    continue;
                var peakY = size.Y - Math.Max(MinBarHeight, peaks[i] * size.Y);
                if (peakY < size.Y - height - PeakThickness)
                {
                    context.DrawRectangle(
                        _peakBrush, null,
                        new Rect(x, peakY, barWidth, PeakThickness), radius, radius);
                }
            }
        }

        private void RenderCompact(ImmediateDrawingContext context, Vector size, float[] levels)
        {
            var count = levels.Length;
            var area = new Size(size.X, size.Y);
            for (var i = 0; i < count; i++)
            {
                var bar = CompactBar(i, count, levels[i], area, CompactBarGap, MinBarHeight);
                if (bar.Width <= 0)
                    return;
                var radius = Math.Min(bar.Width / 2, 1.5);
                context.DrawRectangle(_barBrush, null, bar, radius, radius);
            }
        }

        private void RecordFrame(TimeSpan now, double dt, bool drew)
        {
            if (_traceFrames == 0)
                _traceStartedAt = now;
            _traceFrames++;
            if (drew)
                _traceDraws++;
            _traceWorstMs = Math.Max(_traceWorstMs, dt * 1000);

            var elapsed = (now - _traceStartedAt).TotalSeconds;
            if (elapsed >= 5)
            {
                AppLog.Write("viz", string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{_traceFrames / elapsed:F1} fps avg, {_traceDraws / elapsed:F1} draws/s, worst frame {_traceWorstMs:F1} ms"));
                _traceFrames = 0;
                _traceDraws = 0;
                _traceWorstMs = 0;
            }
        }
    }
}
