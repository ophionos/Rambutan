using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Rambutan.App.Controls;

/// <summary>
/// "Activate this row": double-click on any items control, Enter on a selected
/// ListBox row, or Enter on a focused row of a plain items control, executes
/// <c>Command</c> with the row's item as the parameter.
/// Enter gives keyboard users a one-key route in place of arrow, Tab, Tab, Space.
/// <code>
/// &lt;ListBox controls:ItemActivation.Command="{Binding PlayFromCommand}" … /&gt;
/// </code>
/// </summary>
public static class ItemActivation
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, ICommand?>("Command", typeof(ItemActivation));

    static ItemActivation()
    {
        CommandProperty.Changed.AddClassHandler<ItemsControl>((control, e) =>
        {
            control.DoubleTapped -= OnDoubleTapped;
            control.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            if (e.NewValue is not null)
            {
                control.DoubleTapped += OnDoubleTapped;
                // handledEventsToo: ListBox marks Enter handled (it selects the
                // focused row), and "selected" is exactly the row to play. A focused
                // row button's own Enter is filtered out by source in OnKeyDown.
                control.AddHandler(InputElement.KeyDownEvent, OnKeyDown, handledEventsToo: true);
            }
        });
    }

    public static ICommand? GetCommand(ItemsControl control) => control.GetValue(CommandProperty);

    public static void SetCommand(ItemsControl control, ICommand? value) =>
        control.SetValue(CommandProperty, value);

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ItemsControl control || e.Source is not Visual source)
            return;

        // A double-click on a row's own buttons (▶, +, ♥) is two clicks on that
        // button, not a request to also play the row.
        if (IsInsideButton(source, control))
            return;

        // Row content inherits its item as DataContext; anything else (padding,
        // scrollbar, an empty area) carries the page's and isn't an item.
        var item = (source as StyledElement)?.DataContext;
        if (item is null || !control.Items.Contains(item))
            return;

        e.Handled = Execute(control, item);
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || sender is not ItemsControl control)
            return;

        if (e.Source is Visual source && IsInsideButton(source, control))
            return;

        // A ListBox plays its selected row; a plain items control, whose rows
        // are focus stops of their own (the artist page), plays the focused one.
        var item = control is ListBox list
            ? list.SelectedItem
            : (e.Source as StyledElement)?.DataContext;
        if (item is null || !control.Items.Contains(item))
            return;

        e.Handled = Execute(control, item) || e.Handled;
    }

    private static bool Execute(ItemsControl control, object item)
    {
        if (GetCommand(control) is not { } command || !command.CanExecute(item))
            return false;
        command.Execute(item);
        return true;
    }

    private static bool IsInsideButton(Visual source, Visual boundary)
    {
        for (Visual? v = source; v is not null && v != boundary; v = v.GetVisualParent())
        {
            if (v is Button)
                return true;
        }
        return false;
    }
}
