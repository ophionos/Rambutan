using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Visualizer;
using Rambutan.Audio;
using SkiaSharp;

namespace Rambutan.App.Controls;

/// <summary>
/// Spectrum bars fed from <see cref="FftAnalyzer"/>: a
/// CompositionCustomVisual polls the latest frame per animation frame and draws
/// on the render thread — no UI-thread work, no per-frame allocation. Smoothing
/// (attack/decay envelopes, peak-hold) lives here, not in the analyzer, which
/// keeps emitting raw bands for other consumers (overlay).
/// The full-size visualizer draws through an <see cref="IVisualizerRenderer"/> on
/// Skia's canvas, and with Avalonia's primitives where Skia isn't the renderer
/// (headless tests).
/// With <see cref="Groups"/> set it draws a compact meter instead: a few thin bars
/// without peaks, grown up and down from the middle, for small places like the
/// player bar's artwork. The compact meter and <see cref="OverlayBars"/> draw
/// with primitives only.
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

    /// <summary>The style the full-size visualizer draws in; bars by default.
    /// The compact meter and the overlay preview are always bars.</summary>
    public static readonly StyledProperty<Services.VisualizerStyle> VisualizerStyleProperty =
        AvaloniaProperty.Register<VisualizerControl, Services.VisualizerStyle>(nameof(VisualizerStyle));

    public Services.VisualizerStyle VisualizerStyle
    {
        get => GetValue(VisualizerStyleProperty);
        set => SetValue(VisualizerStyleProperty, value);
    }

    internal const double BarGap = 3;
    internal const double MinBarHeight = 2;
    internal const double PeakThickness = 2;

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
    private Window? _window;
    private bool _polling;

    /// <summary>Whether the bars poll the analyzer and draw: while active and
    /// while the window shows. A minimized window, or one hidden in the tray,
    /// still gets animation frames, so it would keep the analysis and the
    /// redraws running for nobody.</summary>
    internal bool IsPolling => _polling;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
            _window.PropertyChanged += OnWindowPropertyChanged;
        if (ElementComposition.GetElementVisual(this) is not { } elementVisual)
            return;

        _visual = elementVisual.Compositor.CreateCustomVisual(new BarsHandler());
        ElementComposition.SetElementChildVisual(this, _visual);
        _visual.Size = new(Bounds.Width, Bounds.Height);
        _visual.SendHandlerMessage(CurrentPalette());
        _visual.SendHandlerMessage(new BarsHandler.Compact(Groups));
        _visual.SendHandlerMessage(new BarsHandler.Overlay(OverlayBars, ShowPeaks));
        _visual.SendHandlerMessage(new BarsHandler.Pacing(MaxFrameRate));
        _visual.SendHandlerMessage(new BarsHandler.Style(VisualizerRenderers.Create(VisualizerStyle)));
        _polling = false;
        UpdatePolling();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null)
            _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
        _visual?.SendHandlerMessage(BarsHandler.ReleaseMessage);
        _polling = false;
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty)
            UpdatePolling();
    }

    private void UpdatePolling()
    {
        var shown = _window is not { } window
            || (window.IsVisible && window.WindowState != WindowState.Minimized);
        var polling = _visual is not null && IsActive && shown;
        if (polling == _polling)
            return;
        _polling = polling;
        _visual?.SendHandlerMessage(polling ? BarsHandler.StartMessage : BarsHandler.StopMessage);
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
            UpdatePolling();
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
        else if (change.Property == VisualizerStyleProperty)
        {
            _visual?.SendHandlerMessage(new BarsHandler.Style(VisualizerRenderers.Create(VisualizerStyle)));
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
        /// <summary>Stops and disposes the renderer: the visual is going away.</summary>
        public static readonly object ReleaseMessage = new();
        public sealed record Palette(Color Accent, Color? Peak);
        public sealed record Compact(int Groups);
        public sealed record Overlay(int Bars, bool Peaks);
        public sealed record Pacing(int MaxFrameRate);
        /// <summary>The full-size style, built on the UI thread; the handler owns
        /// it from then on and disposes it.</summary>
        public sealed record Style(IVisualizerRenderer Renderer);

        private const double CompactBarGap = 3;

        // Rise fast enough to feel live, fall slow enough to read as motion.
        private const double AttackTau = 0.035;
        private const double DecayTau = 0.16;
        private const double PeakFallPerSecond = 0.9;

        // How an animated style winds down after the feed stops.
        private const double ActivityDecayTau = 0.4;
        private const float ActivityRest = 0.005f;

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
        private VisualizerPalette _palette = new(new SKColor(0xFF, 0x4E, 0x45), DefaultPeak);
        private bool _running;
        private long _version = -1;
        private TimeSpan _versionChangedAt;
        private TimeSpan _lastFrameAt;
        private TimeSpan _startedAt;

        // The renderer path: the style, and the inputs it may ask for.
        private IVisualizerRenderer? _renderer;
        private readonly float[] _left = new float[FftAnalyzer.WaveformLength];
        private readonly float[] _right = new float[FftAnalyzer.WaveformLength];
        private readonly float[] _mono = new float[FftAnalyzer.WaveformLength];
        private readonly OnsetDetector _onsets = new();
        private float _activity;
        private readonly SKPaint _layerPaint = new();

        /// <summary>The primitives path's 0.55-opacity white, as one colour.</summary>
        private static readonly SKColor DefaultPeak = new(0xFF, 0xFF, 0xFF, 140);

        // Frame-pacing evidence: opt-in via RAMBUTAN_VIZ_TRACE.
        private static readonly bool Trace =
            Environment.GetEnvironmentVariable("RAMBUTAN_VIZ_TRACE") is not (null or "");
        private double _traceWorstMs;
        private int _traceFrames;
        private int _traceDraws;
        private long _traceAllocated;
        private float _traceScale; // 0 until a style draws through Skia
        private TimeSpan _traceStartedAt;

        /// <summary>Whether the full-size style draws: not the compact meter, not
        /// the overlay's bars.</summary>
        private bool UsesRenderer => _renderer is not null && _grouped.Length == 0 && _overlayLevels.Length == 0;

        public override void OnMessage(object message)
        {
            if (ReferenceEquals(message, StartMessage) && !_running)
            {
                _running = true;
                _lastFrameAt = CompositionNow;
                _versionChangedAt = CompositionNow;
                _startedAt = CompositionNow;
                _onsets.Reset();
                _traceFrames = 0;
                _traceDraws = 0;
                _traceWorstMs = 0;
                _traceAllocated = 0;
                _gate.Reset();
                RegisterForNextAnimationFrameUpdate();
            }
            else if (ReferenceEquals(message, StopMessage))
            {
                _running = false;
            }
            else if (ReferenceEquals(message, ReleaseMessage))
            {
                _running = false;
                _renderer?.Dispose();
                _renderer = null;
                _layerPaint.Dispose();
            }
            else if (message is Style style)
            {
                _renderer?.Dispose();
                _renderer = style.Renderer;
                _gate.Reset();
                Invalidate();
            }
            else if (message is Palette palette)
            {
                _barBrush = new ImmutableSolidColorBrush(palette.Accent);
                _peakBrush = palette.Peak is { } peak
                    ? new ImmutableSolidColorBrush(peak)
                    : new ImmutableSolidColorBrush(Colors.White, 0.55);
                _palette = new VisualizerPalette(
                    ToSkia(palette.Accent),
                    palette.Peak is { } skiaPeak ? ToSkia(skiaPeak) : DefaultPeak);
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

        private static SKColor ToSkia(Color color) => new(color.R, color.G, color.B, color.A);

        public override void OnAnimationFrameUpdate()
        {
            if (!_running)
                return;

            var allocatedBefore = Trace ? GC.GetAllocatedBytesForCurrentThread() : 0;
            var now = CompositionNow;
            var dt = Math.Clamp((now - _lastFrameAt).TotalSeconds, 0.001, 0.1);
            _lastFrameAt = now;

            var inputs = UsesRenderer ? _renderer!.Inputs : VisualizerInputs.None;
            if (Source?.Invoke() is { } analyzer)
            {
                var version = analyzer.CopyLatest(_target);
                if (version != _version)
                {
                    _version = version;
                    _versionChangedAt = now;
                    // Once per analyzer frame: a repeated frame has no flux.
                    if ((inputs & VisualizerInputs.Beat) != 0)
                        _onsets.Update(_target, now.TotalSeconds);
                }
                if ((inputs & VisualizerInputs.Waveform) != 0)
                    analyzer.CopyWaveform(_left, _right);
            }

            var stale = now - _versionChangedAt > StaleAfter;
            // Paused, the output keeps running a while (EngineAudioPlayer's idle
            // timer) and the analyzer keeps reading its silence: a silent feed
            // winds an animated style down like a stopped one.
            var idle = stale || Silent(_target);
            _activity = idle
                ? _activity * (float)Math.Exp(-dt / ActivityDecayTau)
                : _activity + (1 - _activity) * (float)(1 - Math.Exp(-dt / AttackTau));
            if (idle && _activity < ActivityRest)
                _activity = 0;
            if ((inputs & VisualizerInputs.Waveform) != 0)
            {
                // The analyzer repeats its last block when the feed stops; this
                // flattens it with the activity instead of freezing it.
                for (var i = 0; i < _mono.Length; i++)
                {
                    _left[i] *= _activity;
                    _right[i] *= _activity;
                    _mono[i] = (_left[i] + _right[i]) * 0.5f;
                }
            }
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
                if (UsesRenderer && _renderer!.IsAnimated)
                {
                    draw = _gate.ShouldDrawAnimated(now, Resting(idle));
                }
                else
                {
                    var (levels, peaks) = Bars();
                    draw = _gate.ShouldDraw(now, levels, peaks, size.Y);
                }
            }
            if (draw)
                Invalidate();

            if (Trace)
            {
                _traceAllocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                RecordFrame(now, dt, draw);
            }

            RegisterForNextAnimationFrameUpdate();
        }

        private static bool Silent(ReadOnlySpan<float> bands)
        {
            foreach (var band in bands)
            {
                if (band > 0)
                    return false;
            }
            return true;
        }

        /// <summary>Whether an animated style has wound down: the feed stopped or
        /// went silent, its activity reached 0, and the bars fell to the floor.</summary>
        private bool Resting(bool idle)
        {
            if (!idle || _activity > 0)
                return false;
            foreach (var level in _display)
            {
                if (level > 0.001f)
                    return false;
            }
            return true;
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

            if (UsesRenderer && context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is { } skia)
            {
                var allocatedBefore = Trace ? GC.GetAllocatedBytesForCurrentThread() : 0;
                RenderStyle(skia, size);
                if (Trace)
                    _traceAllocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                return;
            }

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

        private void RenderStyle(ISkiaSharpApiLeaseFeature skia, Vector size)
        {
            using var lease = skia.Lease();
            var canvas = lease.SkCanvas;
            // The canvas maps DIPs to device pixels (and carries the visual's
            // offset); its scale is what a style sizes pixel surfaces by.
            var matrix = canvas.TotalMatrix;
            var scale = MathF.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
            _traceScale = scale;

            var inputs = _renderer!.Inputs;
            var waveform = (inputs & VisualizerInputs.Waveform) != 0;
            float level = 0;
            foreach (var band in _display)
                level += band;

            var frame = new VisualizerFrame
            {
                Bands = _display,
                Peaks = _showPeaks ? _peaks : [],
                Waveform = waveform ? _mono : [],
                Left = waveform ? _left : [],
                Right = waveform ? _right : [],
                Beat = (inputs & VisualizerInputs.Beat) != 0 ? _onsets.Pulse(CompositionNow.TotalSeconds) : 0,
                Level = level / _display.Length,
                Activity = _activity,
                Time = (CompositionNow - _startedAt).TotalSeconds,
                Palette = _palette,
            };

            var saved = canvas.Save();
            // Avalonia applies its controls' opacity to its own paints, not to
            // the canvas, so a raw Skia draw would ignore a translucent parent.
            // One layer for the whole style covers paints, shaders and surfaces.
            var opacity = lease.CurrentOpacity;
            if (opacity < 1)
            {
                _layerPaint.Color = new SKColor(0, 0, 0, (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255));
                canvas.SaveLayer(new SKRect(0, 0, (float)size.X, (float)size.Y), _layerPaint);
            }
            _renderer.Render(canvas, new SKSize((float)size.X, (float)size.Y), scale, in frame);
            canvas.RestoreToCount(saved);
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
                    $"{_traceFrames / elapsed:F1} fps avg, {_traceDraws / elapsed:F1} draws/s, worst frame {_traceWorstMs:F1} ms, {_traceAllocated / (double)_traceFrames:F0} B allocated per frame{(_traceScale > 0 ? $", Skia at {_traceScale:F2}x" : "")}"));
                _traceFrames = 0;
                _traceDraws = 0;
                _traceWorstMs = 0;
                _traceAllocated = 0;
            }
        }
    }
}
