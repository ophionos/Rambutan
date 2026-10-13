using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Controls;

/// <summary>
/// Reorder a ListBox by dragging its rows, or with Alt+↑/↓ on the selected row.
/// The list never edits its own items: it executes
/// <c>Command</c> with a <see cref="ReorderRequest"/> and lets the model move the
/// row, so the list and the queue can't disagree about the order.
/// <code>
/// &lt;ListBox controls:DragReorder.Command="{Binding ReorderCommand}" … /&gt;
/// </code>
/// This is pointer capture, not OS drag-and-drop: the drag never leaves the list,
/// and capture behaves the same on every platform and under synthetic input.
/// </summary>
public static class DragReorder
{
    /// <summary>How far the pointer travels before a press becomes a drag, so a
    /// click (select) or double-click (play) is never read as a tiny move.</summary>
    public const double Threshold = 6;

    /// <summary>Distance from the list's top or bottom edge where a drag scrolls.</summary>
    private const double EdgeScrollZone = 28;

    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<ListBox, ICommand?>("Command", typeof(DragReorder));

    private static readonly AttachedProperty<DragState?> StateProperty =
        AvaloniaProperty.RegisterAttached<ListBox, DragState?>("State", typeof(DragReorder));

    private sealed class DragState
    {
        public int From = -1;
        public Point Start;
        public bool Dragging;
        public int Slot = -1;
        public Control? Indicator;
    }

    static DragReorder()
    {
        CommandProperty.Changed.AddClassHandler<ListBox>((list, e) =>
        {
            list.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            list.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
            list.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
            list.RemoveHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
            list.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            if (e.NewValue is null)
                return;

            // handledEventsToo: ListBoxItem marks the press handled when it selects,
            // and a press on a row is exactly where a drag starts.
            list.AddHandler(InputElement.PointerPressedEvent, OnPressed, handledEventsToo: true);
            list.AddHandler(InputElement.PointerMovedEvent, OnMoved, handledEventsToo: true);
            list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, handledEventsToo: true);
            list.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, handledEventsToo: true);
            // Tunnel: ListBox moves the selection on Alt+↑/↓ too, before a bubbling
            // handler would see the key.
            list.AddHandler(InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            list.SetValue(StateProperty, new DragState());
        });
    }

    public static ICommand? GetCommand(ListBox list) => list.GetValue(CommandProperty);
    public static void SetCommand(ListBox list, ICommand? value) => list.SetValue(CommandProperty, value);

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list || list.GetValue(StateProperty) is not { } state)
            return;

        Reset(list, state);
        if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed || e.Source is not Visual source)
            return;

        // Row buttons (▶, ×) keep their press; a drag starts from the row itself.
        for (Visual? v = source; v is not null && v != list; v = v.GetVisualParent())
        {
            if (v is Button)
                return;
            if (v is ListBoxItem item)
            {
                state.From = list.IndexFromContainer(item);
                state.Start = e.GetPosition(list);
                return;
            }
        }
    }

    private static void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not ListBox list || list.GetValue(StateProperty) is not { From: >= 0 } state)
            return;

        var position = e.GetPosition(list);
        if (!state.Dragging)
        {
            if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
            {
                Reset(list, state);
                return;
            }
            if (Math.Abs(position.Y - state.Start.Y) < Threshold)
                return;

            state.Dragging = true;
            e.Pointer.Capture(list);
            list.ContainerFromIndex(state.From)?.Classes.Set("dragging", true);
        }

        EdgeScroll(list, position.Y);
        state.Slot = SlotAt(list, e);
        ShowIndicator(list, state);
        e.Handled = true;
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not ListBox list || list.GetValue(StateProperty) is not { } state)
            return;

        if (state.Dragging)
        {
            var slot = SlotAt(list, e);
            var from = state.From;
            Reset(list, state);
            e.Pointer.Capture(null);
            e.Handled = true;
            Execute(list, from, TargetIndex(from, slot));
            return;
        }

        Reset(list, state);
    }

    private static void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is ListBox list && list.GetValue(StateProperty) is { Dragging: true } state)
            Reset(list, state);
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox list)
            return;

        if (e.Key == Key.Escape && list.GetValue(StateProperty) is { Dragging: true } state)
        {
            Reset(list, state);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down) ||
            list.SelectedIndex < 0)
        {
            return;
        }

        var from = list.SelectedIndex;
        var to = e.Key == Key.Up ? from - 1 : from + 1;
        e.Handled = true;
        if (to < 0 || to >= list.ItemCount)
            return;
        Execute(list, from, to);
    }

    /// <summary>Insertion slot 0..Count under the pointer: before the first row
    /// whose middle is below it.</summary>
    private static int SlotAt(ListBox list, PointerEventArgs e)
    {
        var count = list.ItemCount;
        for (var i = 0; i < count; i++)
        {
            if (list.ContainerFromIndex(i) is not { IsVisible: true } row)
                continue;
            var y = e.GetPosition(row).Y;
            if (y < row.Bounds.Height / 2)
                return i;
        }
        return count;
    }

    /// <summary>
    /// A slot counts gaps in the list as it looks mid-drag; a move target counts
    /// positions after the row is taken out. Dropping below yourself lands one
    /// earlier than the gap's number.
    /// </summary>
    public static int TargetIndex(int from, int slot) => slot > from ? slot - 1 : slot;

    private static void Execute(ListBox list, int from, int to)
    {
        if (from < 0 || from == to || GetCommand(list) is not { } command)
            return;

        var request = new ReorderRequest(from, to);
        if (!command.CanExecute(request))
            return;
        command.Execute(request);

        // The model re-orders the items asynchronously (queue changes are posted to
        // the UI thread), so keep the moved row selected once it lands.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (to < list.ItemCount)
            {
                list.SelectedIndex = to;
                list.ContainerFromIndex(to)?.Focus();
            }
        });
    }

    private static void EdgeScroll(ListBox list, double y)
    {
        if (list.Scroll is not ScrollViewer scroll)
            return;
        var height = list.Bounds.Height;
        if (y < EdgeScrollZone)
            scroll.Offset = scroll.Offset.WithY(Math.Max(0, scroll.Offset.Y - 12));
        else if (y > height - EdgeScrollZone)
            scroll.Offset = scroll.Offset.WithY(scroll.Offset.Y + 12);
    }

    /// <summary>A 2px accent line on the gap the row would drop into, drawn in the
    /// adorner layer so no row shifts while dragging.</summary>
    private static void ShowIndicator(ListBox list, DragState state)
    {
        var slot = state.Slot;
        var below = slot >= list.ItemCount;
        var anchor = list.ContainerFromIndex(below ? list.ItemCount - 1 : slot);
        var layer = anchor is null ? null : AdornerLayer.GetAdornerLayer(anchor);
        if (anchor is null || layer is null)
            return;

        if (state.Indicator is null)
        {
            state.Indicator = new Border
            {
                Height = 2,
                IsHitTestVisible = false,
                Classes = { "dropindicator" },
            };
        }

        var indicator = state.Indicator;
        indicator.VerticalAlignment = below ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        if (indicator.GetVisualParent() is null)
            layer.Children.Add(indicator);
        AdornerLayer.SetAdornedElement(indicator, anchor);
        indicator.IsVisible = TargetIndex(state.From, slot) != state.From;
    }

    private static void Reset(ListBox list, DragState state)
    {
        if (state.From >= 0)
            list.ContainerFromIndex(state.From)?.Classes.Set("dragging", false);
        if (state.Indicator?.GetVisualParent() is AdornerLayer layer)
            layer.Children.Remove(state.Indicator);
        state.From = -1;
        state.Dragging = false;
        state.Slot = -1;
    }
}
