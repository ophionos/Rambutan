using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Controls;

/// <summary>
/// A sidebar pin's button: its context menu (Open, Move up, Move down, Unpin,
/// from <see cref="SidebarPinsViewModel.MenuFor"/>), and Alt+Up / Alt+Down,
/// which move it and keep the focus on it:
/// <code>
/// &lt;Button controls:SidebarPinMenu.Row="{Binding}" /&gt;
/// </code>
/// The flyout and its keyboard route are <see cref="ContextMenuHost"/>'s.
/// </summary>
public static class SidebarPinMenu
{
    public static readonly AttachedProperty<SidebarPinRowViewModel?> RowProperty =
        AvaloniaProperty.RegisterAttached<Control, SidebarPinRowViewModel?>("Row", typeof(SidebarPinMenu));

    private static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Hooked", typeof(SidebarPinMenu));

    static SidebarPinMenu()
    {
        ContextMenuHost.AddSource(owner => GetRow(owner) is { } row ? row.Owner.MenuFor(row) : []);
        RowProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is null)
                return;
            ContextMenuHost.Install(control);
            if (!control.GetValue(HookedProperty))
            {
                control.SetValue(HookedProperty, true);
                control.AddHandler(InputElement.KeyDownEvent, OnKeyDown);
            }
        });
    }

    public static SidebarPinRowViewModel? GetRow(Control control) => control.GetValue(RowProperty);
    public static void SetRow(Control control, SidebarPinRowViewModel? value) => control.SetValue(RowProperty, value);

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control control || GetRow(control) is not { } row ||
            e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down))
        {
            return;
        }
        e.Handled = true;
        // Found before the move: the list may give the row a new container.
        var list = control.FindAncestorOfType<ItemsControl>();
        if (!row.Owner.Move(row, e.Key == Key.Up ? -1 : 1) || list is null)
            return;
        Dispatcher.UIThread.Post(() => FocusRow(list, row), DispatcherPriority.Loaded);
    }

    private static void FocusRow(ItemsControl list, SidebarPinRowViewModel row)
    {
        if (list.ContainerFromItem(row) is not { } container)
            return;
        var button = container as Button ?? container.FindDescendantOfType<Button>();
        if (button is not null && !button.IsFocused)
            button.Focus(NavigationMethod.Directional);
    }
}
