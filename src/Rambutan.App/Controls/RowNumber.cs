using Avalonia;
using Avalonia.Controls;

namespace Rambutan.App.Controls;

/// <summary>
/// Numbers a list's rows from their position, for surfaces whose items carry no
/// number of their own (a playlist's tracks; an album's come from the catalog).
/// <code>
/// &lt;ListBox controls:RowNumber.IsEnabled="True" … /&gt;
/// &lt;TextBlock Text="{Binding $parent[ListBoxItem].(controls:RowNumber.Number)}" /&gt;
/// </code>
/// The number lives on the container, set when the container is prepared for an
/// item and moved when its index changes, so appended pages (Load more) continue
/// the count and a removed row renumbers the rows after it.
/// </summary>
public static class RowNumber
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, bool>("IsEnabled", typeof(RowNumber));

    /// <summary>The 1-based position of the item a container currently shows.</summary>
    public static readonly AttachedProperty<int> NumberProperty =
        AvaloniaProperty.RegisterAttached<Control, int>("Number", typeof(RowNumber));

    static RowNumber()
    {
        IsEnabledProperty.Changed.AddClassHandler<ItemsControl>((list, e) =>
        {
            list.ContainerPrepared -= OnPrepared;
            list.ContainerIndexChanged -= OnIndexChanged;
            if (e.NewValue is not true)
                return;
            list.ContainerPrepared += OnPrepared;
            list.ContainerIndexChanged += OnIndexChanged;
        });
    }

    public static bool GetIsEnabled(ItemsControl list) => list.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ItemsControl list, bool value) => list.SetValue(IsEnabledProperty, value);

    public static int GetNumber(Control container) => container.GetValue(NumberProperty);

    public static void SetNumber(Control container, int value) => container.SetValue(NumberProperty, value);

    private static void OnPrepared(object? sender, ContainerPreparedEventArgs e) =>
        SetNumber(e.Container, e.Index + 1);

    private static void OnIndexChanged(object? sender, ContainerIndexChangedEventArgs e) =>
        SetNumber(e.Container, e.NewIndex + 1);
}
