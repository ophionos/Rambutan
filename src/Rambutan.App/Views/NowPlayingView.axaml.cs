using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class NowPlayingView : UserControl
{
    /// <summary>From this stage width the artwork and the lyrics sit side by
    /// side; narrower, the lyrics go under the artwork.</summary>
    public const double SideBySideWidth = 840;

    /// <summary>The lyrics under the artwork show this many lines.</summary>
    public const int StackedLines = 3;

    private const double ArtSize = 300;

    /// <summary>The artwork's largest size in fullscreen, where the screen is
    /// larger; it still fits the height.</summary>
    private const double FullscreenArtSize = 480;
    private const double MinArtSize = 120;

    /// <summary>Room the title takes under the artwork, and the stacked lyrics'
    /// footer under their lines, when the artwork is fitted to the height.</summary>
    private const double TitleRoom = 100;
    private const double StackedFooterRoom = 48;

    /// <summary>How far the lyrics reach into the body's side margins, so their
    /// scrollbar sits near the pane's edge rather than inside the margin.</summary>
    public const double LyricsReach = 32;

    private static readonly Cursor NoCursor = new(StandardCursorType.None);

    private NowPlayingViewModel? _vm;
    private TopLevel? _topLevel;
    private Point? _lastPointer;
    private readonly Thickness _stageMargin;
    private readonly Thickness _bodyMargin;

    public NowPlayingView()
    {
        InitializeComponent();
        _stageMargin = StageVisualizer.Margin;
        _bodyMargin = Body.Margin;
        Stage.SizeChanged += (_, _) => ApplyLayout();
        StageVisualizer.PropertyChanged += OnVisualizerPropertyChanged;

        // Fullscreen's controls come back with any input. Tunnelled and
        // handled too: a button or the focus manager takes the event first.
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => _vm?.ShowChrome(), RoutingStrategies.Tunnel, handledEventsToo: true);
        // The pointer over the controls holds them. Keyboard focus doesn't:
        // they hide after the wait like the rest, and the next key brings
        // them back.
        foreach (var part in new Control[] { Header, Transport })
        {
            part.PointerEntered += (_, _) => UpdateHold();
            part.PointerExited += (_, _) => UpdateHold();
        }
    }

    /// <summary>Keys reach the window, not this view, while the focus is
    /// elsewhere; Tab is taken before the window sees it.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnAnyKey, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (_topLevel is WindowBase window)
            window.PropertyChanged += OnWindowPropertyChanged;
        ApplyWindowActive();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnAnyKey);
        if (_topLevel is WindowBase window)
            window.PropertyChanged -= OnWindowPropertyChanged;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowBase.IsActiveProperty)
            ApplyWindowActive();
    }

    private void ApplyWindowActive()
    {
        if (_vm is not null)
            _vm.IsWindowActive = _topLevel is not WindowBase window || window.IsActive;
    }

    private void OnAnyKey(object? sender, KeyEventArgs e) => _vm?.ShowChrome();

    /// <summary>A move only: a window that settles, or a pointer that sits
    /// still, still sends PointerMoved, and that would keep the controls up.</summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var at = e.GetPosition(this);
        if (_lastPointer is { } last && Math.Abs(last.X - at.X) < 1 && Math.Abs(last.Y - at.Y) < 1)
            return;
        _lastPointer = at;
        _vm?.ShowChrome();
    }

    private void UpdateHold() =>
        _vm?.HoldChrome(Header.IsPointerOver || Transport.IsPointerOver);

    private const double FullscreenBodyBottom = 112;

    private void ApplyFullscreen()
    {
        var fullscreen = _vm?.IsFullscreen == true;
        StageVisualizer.Margin = fullscreen ? default : _stageMargin;
        // The lyrics column ends above the transport, so its footer stays clear.
        Body.Margin = fullscreen
            ? new Thickness(_bodyMargin.Left, _bodyMargin.Top, _bodyMargin.Right, FullscreenBodyBottom)
            : _bodyMargin;
        Cursor = _vm?.IsPointerHidden == true ? NoCursor : null;
        if (!fullscreen)
            _lastPointer = null;
    }

    private void OnVisualizerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == VisualizerControl.IsDrawnInSoftwareProperty && _vm is not null)
            _vm.IsDrawnInSoftware = StageVisualizer.IsDrawnInSoftware;
    }

    /// <summary>How the stage is arranged now.</summary>
    public enum StageLayout { ArtOnly, LyricsOnly, SideBySide, Stacked }

    public StageLayout CurrentLayout { get; private set; } = StageLayout.ArtOnly;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as NowPlayingViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelChanged;

        base.OnDataContextChanged(e);
        ApplyWindowActive();
        ApplyLayout();
        ApplyFullscreen();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingViewModel.IsLyricsPaneVisible) or nameof(NowPlayingViewModel.ShowsRing)
            or nameof(NowPlayingViewModel.IsArtworkShown))
            ApplyLayout();
        if (e.PropertyName is nameof(NowPlayingViewModel.IsFullscreen))
            ApplyLayout();
        else if (e.PropertyName is nameof(NowPlayingViewModel.IsFullscreen) or nameof(NowPlayingViewModel.IsPointerHidden))
            ApplyFullscreen();
    }

    private void ApplyLayout()
    {
        var width = Stage.Bounds.Width;
        var height = Stage.Bounds.Height;
        var lyrics = _vm?.IsLyricsPaneVisible == true;
        var art = _vm?.IsArtworkShown == true;
        var layout = !lyrics ? StageLayout.ArtOnly
            : !art ? StageLayout.LyricsOnly
            : width >= SideBySideWidth ? StageLayout.SideBySide
            : StageLayout.Stacked;
        CurrentLayout = layout;

        // Every child back in a cell that exists: a Grid measures hidden
        // children too, and a stale row or column index throws.
        Stage.RowDefinitions.Clear();
        Stage.ColumnDefinitions.Clear();
        Place(Identity, 0);
        Place(LyricsHost, 0);
        Identity.VerticalAlignment = VerticalAlignment.Center;
        Identity.Margin = default;
        LyricsHost.Margin = new Thickness(-LyricsReach, 0, -LyricsReach, 0);
        LyricsHost.LinesShown = null;
        TitleLink.FontSize = 24;

        switch (layout)
        {
            case StageLayout.ArtOnly:
                SetArt(_vm?.IsFullscreen == true ? FitArt(height - TitleRoom) : ArtSize);
                break;

            case StageLayout.LyricsOnly:
                // A compact title over the lyrics column; in fullscreen the
                // title card stands in for it.
                Stage.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Stage.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
                Place(LyricsHost, 1);
                Identity.VerticalAlignment = VerticalAlignment.Top;
                Identity.Margin = new Thickness(0, 0, 0, 16);
                SetArt(0);
                TitleLink.FontSize = 20;
                break;

            case StageLayout.SideBySide:
                // The artwork in the left half, the lyrics in the right, reaching
                // to the pane's edge.
                Stage.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                Stage.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                Place(LyricsHost, 0, column: 1);
                Identity.Margin = new Thickness(0, 0, 24, 0);
                LyricsHost.Margin = new Thickness(0, 0, -LyricsReach, 0);
                SetArt(FitArt(height - TitleRoom));
                break;

            case StageLayout.Stacked:
                // The artwork and title, then a few lines, centred together.
                Stage.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
                Stage.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Stage.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Stage.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
                Place(Identity, 1);
                Place(LyricsHost, 2);
                Identity.Margin = new Thickness(0, 0, 0, 12);
                LyricsHost.LinesShown = StackedLines;
                SetArt(FitArt(height - TitleRoom - StackedLines * LyricsPane.LineHeight - StackedFooterRoom));
                TitleLink.FontSize = 20;
                break;
        }
    }

    /// <summary>The artwork's size in the height left for it; full size before
    /// the first layout.</summary>
    private double FitArt(double room)
    {
        if (room <= 0 && Stage.Bounds.Height <= 0)
            return ArtSize;
        var ring = _vm?.ShowsRing == true ? 2 * RadialRenderer.Margin : 0;
        var max = _vm?.IsFullscreen == true ? FullscreenArtSize : ArtSize;
        return Math.Clamp(room - ring, MinArtSize, max);
    }

    /// <summary>Sizes the artwork; zero folds it away. (Its IsVisible stays bound
    /// to whether the artwork shows at all.)</summary>
    private void SetArt(double size)
    {
        Art.Width = Art.Height = size;
        // The ring reaches past the artwork; the slot keeps the title, the
        // header above and the lyrics beside it clear of it.
        var ring = size > 0 && _vm?.ShowsRing == true ? RadialRenderer.Margin : 0;
        ArtSlot.Margin = size > 0 ? new Thickness(ring, ring, ring, 28 + ring) : default;
        Ring.Margin = new Thickness(-RadialRenderer.Margin);
    }

    private static void Place(Control control, int row, int column = 0)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
    }
}
