using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Metadata;
using Rambutan.App.Localization;
using Rambutan.Core.Layout;

namespace Rambutan.App.Controls;

/// <summary>
/// A page's row of actions, laid out as a horizontal <see cref="StackPanel"/>
/// does while everything fits. In a narrower row the buttons that don't fit
/// move, from the end, into a More button's menu, so each stays reachable
/// (<see cref="ActionRowFit"/> has the rule). Three kinds of item:
/// <list type="bullet">
/// <item>one marked <see cref="KeepProperty"/> always stays (the primary action, a title);</item>
/// <item>one marked <see cref="InMenuProperty"/> is always in the menu;</item>
/// <item>any other <see cref="Button"/> may move into the menu, where its entry
/// carries its command, parameter, name and enabled state, so it needs a
/// command: a button that only opens a flyout has to be kept;</item>
/// <item>anything else (a progress bar, a status text) takes the room left
/// after the buttons, clipped when that is too little.</item>
/// </list>
/// </summary>
public sealed class ActionRow : Control
{
    /// <summary>Whether an item always stays in the row.</summary>
    public static readonly AttachedProperty<bool> KeepProperty =
        AvaloniaProperty.RegisterAttached<ActionRow, Control, bool>("Keep");

    /// <summary>Whether a button is always in the More menu, never in the row,
    /// whatever the width. Like any button in the menu it needs a command.</summary>
    public static readonly AttachedProperty<bool> InMenuProperty =
        AvaloniaProperty.RegisterAttached<ActionRow, Control, bool>("InMenu");

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ActionRow, double>(nameof(Spacing));

    /// <summary>The More button's style classes, so it matches the row's buttons.</summary>
    public static readonly StyledProperty<string> MoreClassesProperty =
        AvaloniaProperty.Register<ActionRow, string>(nameof(MoreClasses), "icon outline");

    private readonly Button _more;
    private readonly Slot _moreSlot;
    private readonly MenuFlyout _menu = new();
    private readonly List<Slot> _slots = [];
    private readonly List<(Slot Slot, double Width)> _placed = [];
    private readonly List<Button> _overflowed = [];

    static ActionRow()
    {
        AffectsMeasure<ActionRow>(SpacingProperty);
        MoreClassesProperty.Changed.AddClassHandler<ActionRow>((row, _) => row.ApplyMoreClasses());
    }

    public ActionRow()
    {
        var icon = new PathIcon { Width = 18, Height = 18 };
        icon.Bind(PathIcon.DataProperty, this.GetResourceObservable("Icon.More"));
        _more = new Button { Content = icon };
        _more.Click += OnMoreClick;
        _moreSlot = new Slot { Child = _more, IsVisible = false };
        LogicalChildren.Add(_moreSlot);
        VisualChildren.Add(_moreSlot);

        Items.CollectionChanged += OnItemsChanged;
        ApplyMoreClasses();
        NameMore();
        Strings.WhenChanged(this, static row => row.NameMore());
    }

    [Content]
    public Avalonia.Controls.Controls Items { get; } = [];

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public string MoreClasses
    {
        get => GetValue(MoreClassesProperty);
        set => SetValue(MoreClassesProperty, value);
    }

    public static bool GetKeep(Control control) => control.GetValue(KeepProperty);

    public static void SetKeep(Control control, bool value) => control.SetValue(KeepProperty, value);

    public static bool GetInMenu(Control control) => control.GetValue(InMenuProperty);

    public static void SetInMenu(Control control, bool value) => control.SetValue(InMenuProperty, value);

    /// <summary>The More button; shown only while a button is in its menu.</summary>
    internal Button MoreButton => _more;

    internal bool ShowsMore => _moreSlot.IsVisible;

    /// <summary>The buttons now in the More menu, in the row's order.</summary>
    internal IReadOnlyList<Button> Overflowed => _overflowed;

    /// <summary>The More menu's entries for the buttons now in it.</summary>
    internal IReadOnlyList<MenuItem> BuildMenu()
    {
        var entries = new List<MenuItem>(_overflowed.Count);
        foreach (var button in _overflowed)
            entries.Add(EntryFor(button));

        return entries;
    }

    /// <summary>The buttons that may move into the menu (not kept, own
    /// <see cref="Visual.IsVisible"/> true), in the row's order, wherever they sit now.</summary>
    internal IEnumerable<Button> MovableButtons()
    {
        foreach (var item in Items)
        {
            if (item is Button { IsVisible: true } button && !GetKeep(button))
                yield return button;
        }
    }

    /// <summary>A menu entry standing for <paramref name="button"/>: its command,
    /// parameter, name, icon and enabled state.</summary>
    internal static MenuItem EntryFor(Button button)
    {
        var entry = new MenuItem
        {
            Header = NameOf(button),
            Command = button.Command,
            CommandParameter = button.CommandParameter,
            IsEnabled = button.IsEnabled,
        };
        if (IconOf(button) is { } geometry)
            entry.Icon = new PathIcon { Data = geometry, Width = 14, Height = 14 };
        return entry;
    }

    private enum Kind
    {
        Keep,
        Action,
        Extra,
        Menu,
    }

    private static Kind KindOf(Control item) =>
        GetKeep(item) ? Kind.Keep
        : item is Button ? (GetInMenu(item) ? Kind.Menu : Kind.Action)
        : Kind.Extra;

    private void ApplyMoreClasses()
    {
        _more.Classes.Clear();
        _more.Classes.AddRange(MoreClasses.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private void NameMore()
    {
        var name = Strings.Get("common.moreActions");
        AutomationProperties.SetName(_more, name);
        ToolTip.SetTip(_more, name);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var slot in _slots)
        {
            if (slot.Child is { } item)
                item.PropertyChanged -= OnItemPropertyChanged;
            slot.Child = null;
            VisualChildren.Remove(slot);
            LogicalChildren.Remove(slot);
        }

        _slots.Clear();
        _placed.Clear();
        _overflowed.Clear();

        foreach (var item in Items)
        {
            var slot = new Slot { Child = item };
            item.PropertyChanged += OnItemPropertyChanged;
            _slots.Add(slot);
            LogicalChildren.Add(slot);
            VisualChildren.Add(slot);
        }

        InvalidateMeasure();
    }

    // A button in the menu sits in a hidden slot, whose layout tells the row
    // nothing, so the row listens for what puts the button in or out of the menu.
    private void OnItemPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == KeepProperty || e.Property == InMenuProperty)
            InvalidateMeasure();
    }

    // Filled before it shows: a menu filled from its Opening event reported
    // open and never appeared on X11.
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

    private static string NameOf(Button button)
    {
        if (AutomationProperties.GetName(button) is { Length: > 0 } name)
            return name;
        if (button.Content is string text)
            return text;
        foreach (var descendant in button.GetLogicalDescendants())
        {
            if (descendant is TextBlock { Text: { Length: > 0 } label })
                return label;
        }

        return ToolTip.GetTip(button) as string ?? string.Empty;
    }

    private static Geometry? IconOf(Button button)
    {
        foreach (var descendant in button.GetLogicalDescendants())
        {
            if (descendant is PathIcon { IsVisible: true, Data: { } geometry })
                return geometry;
        }

        return null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var spacing = Spacing;
        // Measured directly, as the buttons below are: a hidden slot measures as nothing.
        _more.Measure(Size.Infinity);
        var moreWidth = _more.DesiredSize.Width;

        Span<double> actions = _slots.Count <= 32 ? stackalloc double[_slots.Count] : new double[_slots.Count];
        var actionCount = 0;
        var pinnedWidth = 0.0;
        var pinnedCount = 0;
        var menuOnly = 0;
        var height = 0.0;
        foreach (var slot in _slots)
        {
            var item = slot.Child!;
            var kind = KindOf(item);
            slot.ClipToBounds = kind == Kind.Extra;
            if (!item.IsVisible || kind == Kind.Extra)
                continue;
            if (kind == Kind.Menu)
            {
                menuOnly++;
                continue;
            }

            item.Measure(Size.Infinity);
            height = Math.Max(height, item.DesiredSize.Height);
            if (kind == Kind.Keep)
            {
                pinnedWidth += item.DesiredSize.Width;
                pinnedCount++;
            }
            else
            {
                actions[actionCount++] = item.DesiredSize.Width;
            }
        }

        actions = actions[..actionCount];
        // The More button is there anyway while a button lives only in its menu.
        var kept = double.IsInfinity(availableSize.Width)
            ? actionCount
            : ActionRowFit.Kept(availableSize.Width, pinnedWidth, pinnedCount, actions, spacing, moreWidth,
                alwaysMore: menuOnly > 0);
        var hasMore = kept < actionCount || menuOnly > 0;

        // What the kept buttons take, so the extras share only what is left.
        var taken = pinnedWidth + (hasMore ? moreWidth : 0);
        for (var i = 0; i < kept; i++)
            taken += actions[i];
        var left = availableSize.Width - taken - Math.Max(0, pinnedCount + kept + (hasMore ? 1 : 0) - 1) * spacing;

        _placed.Clear();
        _overflowed.Clear();
        Slot? firstMoved = null;
        var seen = 0;
        foreach (var slot in _slots)
        {
            var item = slot.Child!;
            if (!item.IsVisible)
            {
                slot.IsVisible = true;
                continue;
            }

            var kind = KindOf(item);
            if (kind == Kind.Menu || (kind == Kind.Action && seen++ >= kept))
            {
                if (firstMoved is null)
                {
                    firstMoved = slot;
                    _moreSlot.IsVisible = true;
                    _moreSlot.Measure(Size.Infinity);
                    _placed.Add((_moreSlot, moreWidth));
                    height = Math.Max(height, _moreSlot.DesiredSize.Height);
                }

                _overflowed.Add((Button)item);
                slot.IsVisible = false;
                continue;
            }

            slot.IsVisible = true;
            if (kind == Kind.Extra)
            {
                var room = Math.Max(0, _placed.Count > 0 ? left - spacing : left);
                slot.Measure(new Size(room, double.PositiveInfinity));
                var width = slot.DesiredSize.Width;
                // An extra with no room keeps its place in the tree (a status
                // text is still read out) and takes none in the row.
                if (width <= 0)
                    continue;
                left = room - width;
                height = Math.Max(height, slot.DesiredSize.Height);
                _placed.Add((slot, width));
            }
            else
            {
                slot.Measure(Size.Infinity);
                _placed.Add((slot, slot.DesiredSize.Width));
            }
        }

        if (firstMoved is null)
            _moreSlot.IsVisible = false;
        else
            PutMoreBefore(firstMoved);

        var total = Math.Max(0, _placed.Count - 1) * spacing;
        foreach (var (_, width) in _placed)
            total += width;
        return new Size(total, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (var (slot, width) in _placed)
        {
            slot.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width + Spacing;
        }

        return finalSize;
    }

    // Tab and screen readers follow the order of the visual children, so the
    // More button sits where the first button it holds was.
    private void PutMoreBefore(Slot slot)
    {
        var from = VisualChildren.IndexOf(_moreSlot);
        var to = VisualChildren.IndexOf(slot);
        if (from < to)
            to--;
        if (from != to)
            VisualChildren.Move(from, to);
    }

    /// <summary>Holds one item. The row hides the slot, never the item, whose
    /// own visibility stays the page's to bind.</summary>
    private sealed class Slot : Decorator;
}
