using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Layout;

namespace Rambutan.App.Views;

public partial class NowPlayingView : UserControl
{
    /// <summary>From this stage width the artwork and the lyrics sit side by
    /// side; narrower, the lyrics go under the artwork.</summary>
    public const double SideBySideWidth = 840;

    /// <summary>How far the lyrics reach into the body's side margins, so their
    /// scrollbar sits near the pane's edge rather than inside the margin.</summary>
    public const double LyricsReach = 32;

    /// <summary>Between the artwork and the title under it.</summary>
    private const double ArtGap = 28;

    /// <summary>Between the artwork and the lyrics beside it.</summary>
    private const double SideGap = 24;

    /// <summary>Between the header's cover and its title.</summary>
    private const double HeaderGap = 12;

    /// <summary>The widest the title and the artist line get under the artwork.</summary>
    private const double TextMaxWidth = 560;

    private const double ArtCorner = 12;
    private const double HeaderArtCorner = 6;

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
        // The artwork's size leaves room for the title as it wraps: a new
        // track's title can take another line, or the artist line wrap.
        TitleLink.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged)
                ApplyLayout();
        };
        Credits.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged)
                ApplyLayout();
        };
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
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.IsArtworkCompact = false;
        }
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

    private bool _applying;

    /// <summary>Arranges the stage for what shows and the room it has. Each
    /// layout starts from the same reset, since the user moves between all
    /// four. Setting the compact cover changes the ring, which asks for a
    /// layout again; this one already accounts for it.</summary>
    private void ApplyLayout()
    {
        if (_applying)
            return;
        _applying = true;
        try
        {
            ArrangeStage();
        }
        finally
        {
            _applying = false;
        }
    }

    private void ArrangeStage()
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
        if (_vm is not null)
            _vm.IsArtworkCompact = layout == StageLayout.Stacked;

        // Every child back in a cell that exists: a Grid measures hidden
        // children too, and a stale row or column index throws.
        Stage.RowDefinitions.Clear();
        Stage.ColumnDefinitions.Clear();
        Place(Identity, 0);
        Place(LyricsHost, 0);
        Identity.VerticalAlignment = VerticalAlignment.Center;
        Identity.HorizontalAlignment = HorizontalAlignment.Center;
        Identity.Margin = default;
        LyricsHost.Margin = new Thickness(-LyricsReach, 0, -LyricsReach, 0);
        ArrangeIdentity(header: false);

        switch (layout)
        {
            case StageLayout.ArtOnly:
                FitArt(width, height);
                break;

            case StageLayout.LyricsOnly:
                // A compact title over the lyrics column; in fullscreen the
                // title card stands in for it.
                Stage.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Stage.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
                Place(LyricsHost, 1);
                Identity.VerticalAlignment = VerticalAlignment.Top;
                Identity.Margin = new Thickness(0, 0, 0, 16);
                SetArt(0, 0);
                TitleLink.FontSize = NowPlayingLayout.CompactTitleSize;
                break;

            case StageLayout.SideBySide:
                // The artwork in the left half, the lyrics in the right, reaching
                // to the pane's edge.
                Stage.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                Stage.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                Place(LyricsHost, 0, column: 1);
                Identity.Margin = new Thickness(0, 0, SideGap, 0);
                LyricsHost.Margin = new Thickness(0, 0, -LyricsReach, 0);
                FitArt(width / 2 - SideGap, height);
                break;

            case StageLayout.Stacked:
                // The lyrics take the pane, under a header of the cover, the
                // title and the artist in a line. The cover is too small for
                // the ring (IsArtworkCompact), so the stage draws bars.
                Stage.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Stage.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
                Place(LyricsHost, 1);
                Identity.VerticalAlignment = VerticalAlignment.Top;
                Identity.HorizontalAlignment = HorizontalAlignment.Stretch;
                Identity.Margin = new Thickness(0, 0, 0, 16);
                ArrangeIdentity(header: true);
                TitleLink.FontSize = NowPlayingLayout.CompactTitleSize;
                Art.Width = Art.Height = NowPlayingLayout.HeaderArt;
                Art.CornerRadius = new CornerRadius(HeaderArtCorner);
                ArtSlot.Margin = new Thickness(0, 0, HeaderGap, 0);
                break;
        }
    }

    /// <summary>The artwork's parts as a column (artwork, title, artist), or
    /// as the header's row: the cover on the left, the title over the artist
    /// line beside it, the two centred on the cover. The same controls either
    /// way, so their commands, names and focus order stay single.</summary>
    private void ArrangeIdentity(bool header)
    {
        Identity.RowDefinitions.Clear();
        Identity.ColumnDefinitions.Clear();
        if (!header)
        {
            Identity.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Identity.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Identity.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Place(ArtSlot, 0);
            Place(TitleLink, 1);
            Place(Credits, 2);
            Grid.SetRowSpan(ArtSlot, 1);
            ArtSlot.HorizontalAlignment = HorizontalAlignment.Center;
            ArtSlot.ClearValue(VerticalAlignmentProperty);
            Art.CornerRadius = new CornerRadius(ArtCorner);
            TitleLink.HorizontalAlignment = HorizontalAlignment.Center;
            TitleLink.MaxWidth = TextMaxWidth;
            TitleText.TextAlignment = TextAlignment.Center;
            TitleText.TextWrapping = TextWrapping.Wrap;
            TitleText.MaxLines = 2;
            Credits.HorizontalAlignment = HorizontalAlignment.Center;
            Credits.Margin = new Thickness(0, 6, 0, 0);
            return;
        }

        // The title and the artist line in the middle rows, with equal room
        // above and below them beside the cover, so the pair is centred on it.
        Identity.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Identity.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        Identity.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        Identity.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Identity.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Identity.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        Place(ArtSlot, 0);
        Grid.SetRowSpan(ArtSlot, 4);
        Place(TitleLink, 1, column: 1);
        Place(Credits, 2, column: 1);
        ArtSlot.HorizontalAlignment = HorizontalAlignment.Left;
        ArtSlot.VerticalAlignment = VerticalAlignment.Center;
        TitleLink.HorizontalAlignment = HorizontalAlignment.Left;
        TitleLink.MaxWidth = double.PositiveInfinity;
        TitleText.TextAlignment = TextAlignment.Left;
        TitleText.TextWrapping = TextWrapping.NoWrap;
        TitleText.MaxLines = 1;
        Credits.HorizontalAlignment = HorizontalAlignment.Left;
        Credits.Margin = new Thickness(0, 2, 0, 0);
    }

    /// <summary>Sizes the artwork and the title for a stage
    /// <paramref name="width"/> wide (the width the artwork may take) and
    /// <paramref name="height"/> high.</summary>
    private void FitArt(double width, double height)
    {
        var ring = _vm?.ShowsRing == true ? RadialRenderer.Margin : 0;
        var fullscreen = _vm?.IsFullscreen == true;
        var fit = NowPlayingLayout.Fit(width, height, ring, fullscreen, size => TextRoom(size, width));
        SetArt(fit.Art, ring);
        TitleLink.FontSize = fit.TitleSize;
    }

    /// <summary>The height under the artwork that a title of
    /// <paramref name="titleSize"/> takes with the artist line, measured as
    /// they wrap in <paramref name="width"/>: one line or two.</summary>
    private double TextRoom(double titleSize, double width)
    {
        TitleLink.FontSize = titleSize;
        // Measured before it is in a window, a link would take its template
        // without the app's styles and keep it. The stage's first size lays
        // it out again.
        if (TopLevel.GetTopLevel(this) is null)
            return ArtGap;
        var available = new Size(Math.Clamp(width, 0, TextMaxWidth), double.PositiveInfinity);
        // The text itself: a font size inherited by it leaves the link's own
        // measure valid, so measuring the link would give its old height. The
        // link adds no padding or border around it.
        TitleText.Measure(available);
        Credits.Measure(available);
        // DesiredSize includes the artist line's margin.
        return ArtGap + TitleText.DesiredSize.Height + Credits.DesiredSize.Height;
    }

    /// <summary>Sizes the artwork, keeping <paramref name="ring"/> clear around
    /// it for the radial visualizer; zero folds it away. (Its IsVisible stays
    /// bound to whether the artwork shows at all.)</summary>
    private void SetArt(double size, double ring)
    {
        Art.Width = Art.Height = size;
        // The ring reaches past the artwork; the slot keeps the title, the
        // header above and the lyrics beside it clear of it.
        ArtSlot.Margin = size > 0 ? new Thickness(ring, ring, ring, ArtGap + ring) : default;
        Ring.Margin = new Thickness(-RadialRenderer.Margin);
    }

    private static void Place(Control control, int row, int column = 0)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
    }
}
