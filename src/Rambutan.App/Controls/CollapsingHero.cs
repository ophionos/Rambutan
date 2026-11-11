using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.VisualTree;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;
using Rambutan.Core.Layout;

namespace Rambutan.App.Controls;

/// <summary>
/// A page's header above its track list: the full hero (cover, title, metadata,
/// <see cref="ActionRow"/>) while the list is at its top, and a thin bar (small
/// cover, title, Play, More) once it is scrolled, so the list gets the room.
/// <see cref="HeroCollapse"/> has the rule. The bar takes the hero's own row,
/// never a place over the rows, and only one of the two is shown, so only one
/// is in the Tab order.
/// <list type="bullet">
/// <item>The bar's Play mirrors the hero row's kept button (its command,
/// parameter, enabled state and name); its More menu lists the row's other
/// visible buttons as <see cref="ActionRow"/>'s own menu does.</item>
/// <item>Focus inside the part that goes moves to its counterpart in the part
/// that comes: Play to Play, anything else to More (or, coming back, to the
/// row's first action).</item>
/// <item>The part that comes fades in over <see cref="PageTransitions.Fade"/>;
/// at once under reduced motion, and on a page shown again at a remembered
/// offset (<see cref="ScrollMemory"/>).</item>
/// <item>Nothing changes while a pointer is pressed in the list (a drag that
/// reorders rows, the scroll bar's thumb), so rows never move under it.</item>
/// <item>A hero whose own <see cref="Visual.IsVisible"/> is false (a failed
/// first load) shows neither part.</item>
/// </list>
/// </summary>
public sealed class CollapsingHero : Control
{
    public static readonly StyledProperty<Control?> HeroProperty =
        AvaloniaProperty.Register<CollapsingHero, Control?>(nameof(Hero));

    /// <summary>The list whose scrolling collapses the hero.</summary>
    public static readonly StyledProperty<ListBox?> ListProperty =
        AvaloniaProperty.Register<CollapsingHero, ListBox?>(nameof(List));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<CollapsingHero, string?>(nameof(Title));

    public static readonly StyledProperty<string?> CoverUrlProperty =
        AvaloniaProperty.Register<CollapsingHero, string?>(nameof(CoverUrl));

    /// <summary>Drawn under the bar's cover, for a page whose hero has a placeholder.</summary>
    public static readonly StyledProperty<Geometry?> CoverIconProperty =
        AvaloniaProperty.Register<CollapsingHero, Geometry?>(nameof(CoverIcon));

    /// <summary>Keeps the hero up while it shows something the bar can't (a
    /// failed page's Try again).</summary>
    public static readonly StyledProperty<bool> HoldExpandedProperty =
        AvaloniaProperty.Register<CollapsingHero, bool>(nameof(HoldExpanded));

    public static readonly DirectProperty<CollapsingHero, bool> IsCollapsedProperty =
        AvaloniaProperty.RegisterDirect<CollapsingHero, bool>(nameof(IsCollapsed), hero => hero.IsCollapsed);

    private const double BarHeight = 56;

    private readonly Slot _heroSlot = new();
    private readonly Border _bar;
    private readonly Button _play;
    private readonly Button _more;
    private readonly MenuFlyout _menu = new();
    private readonly List<IDisposable> _playLinks = [];
    private ListBox? _list;
    private ActionRow? _row;
    private Button? _heroPlay;
    private double _heroHeight;
    private double? _restoreTarget;
    private bool _isCollapsed;
    private bool _pressed;

    public CollapsingHero()
    {
        var placeholder = new PathIcon { Width = 18, Height = 18, Opacity = 0.35 };
        placeholder.Bind(PathIcon.DataProperty, this.GetObservable(CoverIconProperty));
        var image = new RemoteImage { Stretch = Stretch.UniformToFill, DecodeWidth = 96 };
        image.Bind(RemoteImage.UrlProperty, this.GetObservable(CoverUrlProperty));
        var art = new Border
        {
            Classes = { "herobarart" },
            Width = 40,
            Height = 40,
            Child = new Panel { Children = { placeholder, image } },
        };

        var title = new TextBlock
        {
            Classes = { "herobartitle" },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0),
        };
        title.Bind(TextBlock.TextProperty, this.GetObservable(TitleProperty));
        Grid.SetColumn(title, 1);

        var playIcon = new PathIcon { Width = 18, Height = 18 };
        playIcon.Bind(PathIcon.DataProperty, this.GetResourceObservable("Icon.Play"));
        _play = new Button { Classes = { "cardplay" }, Width = 40, Height = 40, Content = playIcon };
        Grid.SetColumn(_play, 2);

        var moreIcon = new PathIcon { Width = 18, Height = 18 };
        moreIcon.Bind(PathIcon.DataProperty, this.GetResourceObservable("Icon.More"));
        _more = new Button { Classes = { "icon", "outline" }, Content = moreIcon, Margin = new Thickness(8, 0, 0, 0) };
        _more.Click += OnMoreClick;
        Grid.SetColumn(_more, 3);

        _bar = new Border
        {
            Classes = { "herobar" },
            Height = BarHeight,
            Margin = new Thickness(4, 0, 16, 8),
            Padding = new Thickness(8),
            IsVisible = false,
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                Children = { art, title, _play, _more },
            },
        };

        LogicalChildren.Add(_heroSlot);
        VisualChildren.Add(_heroSlot);
        LogicalChildren.Add(_bar);
        VisualChildren.Add(_bar);

        NameMore();
        Strings.WhenChanged(this, static hero => hero.NameMore());
    }

    [Content]
    public Control? Hero
    {
        get => GetValue(HeroProperty);
        set => SetValue(HeroProperty, value);
    }

    public ListBox? List
    {
        get => GetValue(ListProperty);
        set => SetValue(ListProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? CoverUrl
    {
        get => GetValue(CoverUrlProperty);
        set => SetValue(CoverUrlProperty, value);
    }

    public Geometry? CoverIcon
    {
        get => GetValue(CoverIconProperty);
        set => SetValue(CoverIconProperty, value);
    }

    public bool HoldExpanded
    {
        get => GetValue(HoldExpandedProperty);
        set => SetValue(HoldExpandedProperty, value);
    }

    /// <summary>Whether the bar shows in place of the hero.</summary>
    public bool IsCollapsed => _isCollapsed;

    /// <summary>The part that holds the hero; hidden while collapsed.</summary>
    internal Control HeroPart => _heroSlot;

    /// <summary>The bar; hidden while expanded.</summary>
    internal Control Bar => _bar;

    internal Button BarPlayButton => _play;

    internal Button BarMoreButton => _more;

    /// <summary>The More menu's entries: the hero row's visible actions but Play.</summary>
    internal IReadOnlyList<MenuItem> BuildMenu() =>
        _row is null ? [] : _row.MovableButtons().Select(ActionRow.EntryFor).ToList();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HeroProperty)
        {
            if (change.OldValue is Control old)
                old.PropertyChanged -= OnHeroPropertyChanged;
            _heroSlot.Child = change.NewValue as Control;
            if (change.NewValue is Control hero)
                hero.PropertyChanged += OnHeroPropertyChanged;
            LinkPlay();
            InvalidateMeasure();
        }
        else if (change.Property == ListProperty)
        {
            Watch(change.GetNewValue<ListBox?>());
        }
        else if (change.Property == HoldExpandedProperty)
        {
            Evaluate();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LinkPlay();
    }

    private void OnHeroPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty)
            return;
        InvalidateMeasure();
        Evaluate();
    }

    private void NameMore()
    {
        var name = Strings.Get("common.moreActions");
        AutomationProperties.SetName(_more, name);
        ToolTip.SetTip(_more, name);
    }

    // The bar's Play follows the hero's: the same command, so there is one
    // action under two buttons, and the same enabled state and name.
    private void LinkPlay()
    {
        var row = Hero?.GetLogicalDescendants().OfType<ActionRow>().FirstOrDefault();
        var play = row?.Items.OfType<Button>().FirstOrDefault(ActionRow.GetKeep);
        _row = row;
        if (ReferenceEquals(play, _heroPlay))
            return;

        foreach (var link in _playLinks)
            link.Dispose();
        _playLinks.Clear();
        _heroPlay = play;
        _play.IsVisible = play is not null;
        if (play is null)
            return;

        _playLinks.Add(_play.Bind(Button.CommandProperty, play.GetObservable(Button.CommandProperty)));
        _playLinks.Add(_play.Bind(Button.CommandParameterProperty, play.GetObservable(Button.CommandParameterProperty)));
        _playLinks.Add(_play.Bind(IsEnabledProperty, play.GetObservable(IsEnabledProperty)));
        _playLinks.Add(_play.Bind(AutomationProperties.NameProperty, play.GetObservable(AutomationProperties.NameProperty)));
        _playLinks.Add(_play.Bind(ToolTip.TipProperty, play.GetObservable(ToolTip.TipProperty)));
    }

    private void Watch(ListBox? list)
    {
        if (_list is { } old)
        {
            old.RemoveHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
            old.RemoveHandler(PointerPressedEvent, OnPressed);
            old.RemoveHandler(PointerReleasedEvent, OnReleased);
            old.RemoveHandler(PointerCaptureLostEvent, OnCaptureLost);
            old.RemoveHandler(PointerWheelChangedEvent, OnWheel);
            old.DataContextChanged -= OnListDataContextChanged;
        }

        _list = list;
        _pressed = false;
        if (list is null)
            return;

        list.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
        // handledEventsToo: a row marks its press handled when it selects, and
        // the scroll bar's thumb takes its own.
        list.AddHandler(PointerPressedEvent, OnPressed, handledEventsToo: true);
        list.AddHandler(PointerReleasedEvent, OnReleased, handledEventsToo: true);
        list.AddHandler(PointerCaptureLostEvent, OnCaptureLost, handledEventsToo: true);
        list.AddHandler(PointerWheelChangedEvent, OnWheel, handledEventsToo: true);
        list.DataContextChanged += OnListDataContextChanged;
        Adopt();
    }

    private void OnListDataContextChanged(object? sender, EventArgs e) => Adopt();

    // A new page (or the same view recycled for another) starts expanded. If it
    // remembers an offset, ScrollMemory restores it once the content can hold
    // it, and that offset shows the bar at once.
    private void Adopt()
    {
        _restoreTarget = (_list?.DataContext as ViewModelBase)?.SavedScrollOffset is > HeroCollapse.Top and var saved
            ? saved
            : null;
        Apply(collapsed: false, offset: null, animate: false);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e) => _pressed = true;

    private void OnReleased(object? sender, PointerReleasedEventArgs e) => Release();

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => Release();

    private void Release()
    {
        if (!_pressed)
            return;
        _pressed = false;
        Evaluate();
    }

    // A wheel turn before the restore means the user scrolls from where the
    // list is, as ScrollMemory then gives up its restore too.
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        _restoreTarget = null;
        _pressed = false;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => Evaluate();

    private void Evaluate()
    {
        if (_list?.Scroll is not ScrollViewer scroll)
            return;

        var hold = HoldExpanded || Hero is not { IsVisible: true };
        if (_pressed && !hold)
            return;

        var offset = scroll.Offset.Y;
        var extent = scroll.Extent.Height;
        var viewport = scroll.Viewport.Height;
        var barHeight = BarHeight + _bar.Margin.Top + _bar.Margin.Bottom;

        if (_restoreTarget is { } target)
        {
            if (Math.Abs(offset - target) <= 0.5)
            {
                _restoreTarget = null;
                if (!_isCollapsed)
                {
                    var restored = HeroCollapse.Restored(offset, extent, viewport, _heroHeight, barHeight, hold);
                    Apply(restored.Collapsed, offset: null, animate: false);
                    return;
                }
            }
            else if (offset > HeroCollapse.Top)
            {
                _restoreTarget = null;
            }
        }

        var step = HeroCollapse.Scrolled(_isCollapsed, offset, extent, viewport, _heroHeight, barHeight, hold);
        Apply(step.Collapsed, step.Offset, animate: true);
    }

    private void Apply(bool collapsed, double? offset, bool animate)
    {
        if (collapsed == _isCollapsed)
            return;

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        Control outgoing = collapsed ? _heroSlot : _bar;
        Control incoming = collapsed ? _bar : _heroSlot;
        var focusLeaves = focused is not null && outgoing.IsVisualAncestorOf(focused);

        SetAndRaise(IsCollapsedProperty, ref _isCollapsed, collapsed);
        if (collapsed)
            _more.IsVisible = _row?.MovableButtons().Any() ?? false;
        incoming.IsVisible = true;
        Fade(incoming, animate && !ReducedMotion);
        if (focusLeaves)
            Counterpart(focused!, collapsed)?.Focus();
        outgoing.IsVisible = false;
        if (focusLeaves && outgoing.IsKeyboardFocusWithin)
            _list?.Focus();

        // Set before the layout that hides the hero, so the list's top and its
        // offset move together and its rows stay where they were.
        if (offset is { } y && _list?.Scroll is ScrollViewer scroll && Math.Abs(scroll.Offset.Y - y) > 0.01)
            scroll.Offset = scroll.Offset.WithY(y);
        InvalidateMeasure();
    }

    private Control? Counterpart(Visual focused, bool collapsing)
    {
        if (collapsing)
        {
            var onPlay = _heroPlay is not null && (focused == _heroPlay || _heroPlay.IsVisualAncestorOf(focused));
            return onPlay || !_more.IsVisible ? _play : _more;
        }

        if (focused == _play || _play.IsVisualAncestorOf(focused))
            return _heroPlay;
        if (_row is { ShowsMore: true })
            return _row.MoreButton;
        return _row?.MovableButtons().FirstOrDefault(button => button.IsEffectivelyVisible) ?? _heroPlay;
    }

    private bool ReducedMotion => TopLevel.GetTopLevel(this)?.Classes.Contains("reducedmotion") ?? true;

    private static void Fade(Control part, bool animate)
    {
        part.Transitions = null;
        part.Opacity = animate ? 0 : 1;
        if (!animate)
            return;
        part.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = PageTransitions.Fade }];
        part.Opacity = 1;
    }

    // Filled before it shows, as ActionRow's: a menu filled from its Opening
    // event reported open and never appeared on X11.
    private void OnMoreClick(object? sender, RoutedEventArgs e)
    {
        if (_menu.IsOpen)
        {
            _menu.Hide();
            return;
        }

        _menu.Items.Clear();
        foreach (var entry in BuildMenu())
            _menu.Items.Add(entry);
        if (_menu.Items.Count > 0)
            _menu.ShowAt(_more);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _heroSlot.Measure(availableSize);
        _bar.Measure(availableSize);
        if (!_isCollapsed)
            _heroHeight = _heroSlot.DesiredSize.Height;
        var shown = _isCollapsed ? _bar.DesiredSize : _heroSlot.DesiredSize;
        return new Size(Math.Max(_heroSlot.DesiredSize.Width, _bar.DesiredSize.Width), shown.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rect = new Rect(finalSize);
        _heroSlot.Arrange(rect);
        _bar.Arrange(rect);
        return finalSize;
    }

    /// <summary>Holds the hero. The control hides the slot, never the hero,
    /// whose own visibility stays the page's to bind.</summary>
    private sealed class Slot : Decorator;
}
