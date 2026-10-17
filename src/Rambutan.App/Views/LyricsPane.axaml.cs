using System;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

/// <summary>
/// The lyrics pane's scrolling. Following playback, the active line is
/// kept about 40 % down the pane, eased into place (at once under reduced
/// motion). Scrolling by hand (wheel, scrollbar, touch, keys) stops the
/// following and shows a "Back to the current line" pill; three seconds without
/// more scrolling, or the pill, or clicking a line, resumes it.
/// </summary>
public partial class LyricsPane : UserControl
{
    /// <summary>How long after the last manual scroll following resumes by itself.</summary>
    public static readonly TimeSpan ResumeAfter = TimeSpan.FromSeconds(3);

    /// <summary>Where the active line's centre sits, as a fraction of the pane's height.</summary>
    public const double AnchorFraction = 0.4;

    /// <summary>Where it sits when the pane shows only a few lines: in the
    /// middle, between the lines before and after it.</summary>
    public const double FewLinesAnchor = 0.5;

    /// <summary>The widest the lines get: wider, a line is hard to follow from
    /// one end to the other. The pane itself can be wider, so its scrollbar
    /// sits at the edge of the space it is given.</summary>
    public const double ColumnMaxWidth = 760;

    /// <summary>The height of a synced line on one row, for
    /// <see cref="LinesShown"/>.</summary>
    public const double LineHeight = 46;

    private static readonly TimeSpan ScrollDuration = TimeSpan.FromMilliseconds(350);

    private readonly DispatcherTimer _resumeTimer;
    private readonly Stopwatch _clock = new();
    private LyricsViewModel? _vm;
    private double _animationFrom;
    private double _animationTo;
    private bool _animating;
    private int? _linesShown;

    public LyricsPane()
    {
        InitializeComponent();

        _resumeTimer = new DispatcherTimer { Interval = ResumeAfter };
        _resumeTimer.Tick += (_, _) => Resume();
        ResumeButton.Click += (_, _) => Resume();

        // Tunnel, and handled events too: the ScrollViewer consumes the wheel and
        // keys it scrolls by, and those are exactly the ones that mean "by hand".
        Scroller.AddHandler(PointerWheelChangedEvent, (_, _) => OnManualScroll(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        Scroller.AddHandler(ScrollGestureEvent, (_, _) => OnManualScroll(),
            RoutingStrategies.Bubble, handledEventsToo: true);
        Scroller.AddHandler(PointerPressedEvent, OnPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        Scroller.AddHandler(KeyDownEvent, OnKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        Scroller.SizeChanged += (_, _) => ApplyLinesMargin();
    }

    /// <summary>False while the listener has scrolled away from the playing line.</summary>
    public bool IsFollowing { get; private set; } = true;

    /// <summary>How many lines the pane shows, under the artwork when it has
    /// little room; null fills the height it is given. The other states
    /// (loading, none, not looked up) keep their own height.</summary>
    public int? LinesShown
    {
        get => _linesShown;
        set
        {
            if (_linesShown == value)
                return;
            _linesShown = value;
            Scroller.MaxHeight = value is { } lines ? lines * LineHeight : double.PositiveInfinity;
            ApplyLinesMargin();
        }
    }

    private double Anchor => _linesShown is null ? AnchorFraction : FewLinesAnchor;

    /// <summary>Room below the last line, so it too can rise to the anchor; and
    /// above the first, except when only a few lines show, where it would push
    /// the last of them out of view.</summary>
    private void ApplyLinesMargin()
    {
        var top = _linesShown is null ? 20 : 0;
        LinesList.Margin = new Thickness(0, top, 0, Scroller.Bounds.Height * (1 - Anchor));
        ScrollToActive(animate: false);
    }

    /// <summary>
    /// The scroll offset that puts a line's centre at <paramref name="anchor"/>
    /// of the viewport, clamped to what the content can scroll to.
    /// </summary>
    public static double TargetOffset(double lineTop, double lineHeight, double viewport, double extent,
        double anchor = AnchorFraction) =>
        Math.Clamp(lineTop + lineHeight / 2 - viewport * anchor, 0, Math.Max(0, extent - viewport));

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.LineSought -= Resume;
        }

        _vm = DataContext as LyricsViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.LineSought += Resume;
        }

        base.OnDataContextChanged(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LyricsViewModel.Lines):
            case nameof(LyricsViewModel.PlainText):
                // New lyrics start at the top and following playback.
                StopFollowingTimer();
                IsFollowing = true;
                ResumeButton.IsVisible = false;
                _animating = false;
                Scroller.Offset = new Vector(Scroller.Offset.X, 0);
                Dispatcher.UIThread.Post(() => ScrollToActive(animate: false), DispatcherPriority.Loaded);
                break;

            case nameof(LyricsViewModel.ActiveLineIndex) when IsFollowing:
                ScrollToActive(animate: true);
                break;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // The scrollbar (thumb, track, arrows) is a manual scroll; a line is a seek.
        if (e.Source is Visual source && source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)
            OnManualScroll();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            OnManualScroll();
    }

    private void OnManualScroll()
    {
        // Only synced lyrics follow playback; plain text is the listener's to scroll.
        if (_vm is not { IsSynced: true })
            return;

        IsFollowing = false;
        _animating = false;
        ResumeButton.IsVisible = true;
        StopFollowingTimer();
        _resumeTimer.Start();
    }

    private void StopFollowingTimer() => _resumeTimer.Stop();

    private void Resume()
    {
        StopFollowingTimer();
        IsFollowing = true;
        ResumeButton.IsVisible = false;
        ScrollToActive(animate: true);
    }

    /// <summary>Brings the active line to the anchor: eased, or at once under
    /// reduced motion, when <paramref name="animate"/> is false, or when the pane
    /// isn't on screen to watch it.</summary>
    public void ScrollToActive(bool animate)
    {
        if (_vm is not { IsSynced: true } || !IsFollowing)
            return;

        var target = 0.0;
        if (_vm.ActiveLineIndex >= 0)
        {
            if (LinesList.ContainerFromIndex(_vm.ActiveLineIndex) is not Control line)
            {
                LinesList.UpdateLayout();
                if (LinesList.ContainerFromIndex(_vm.ActiveLineIndex) is not Control laidOut)
                    return;
                line = laidOut;
            }

            if (Scroller.Content is not Visual content || line.TranslatePoint(default, content) is not { } top)
                return;

            target = TargetOffset(top.Y, line.Bounds.Height, Scroller.Viewport.Height, Scroller.Extent.Height, Anchor);
        }

        var reduced = TopLevel.GetTopLevel(this)?.Classes.Contains("reducedmotion") ?? true;
        if (!animate || reduced || !IsEffectivelyVisible)
        {
            _animating = false;
            Scroller.Offset = new Vector(Scroller.Offset.X, target);
            return;
        }

        _animationFrom = Scroller.Offset.Y;
        _animationTo = target;
        _clock.Restart();
        if (!_animating)
        {
            _animating = true;
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Step);
        }
    }

    private void Step(TimeSpan _)
    {
        if (!_animating)
            return;

        var t = Math.Min(1, _clock.Elapsed / ScrollDuration);
        var eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
        Scroller.Offset = new Vector(Scroller.Offset.X, _animationFrom + (_animationTo - _animationFrom) * eased);

        if (t < 1)
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Step);
        else
            _animating = false;
    }
}
