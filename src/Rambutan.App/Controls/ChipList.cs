using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Rambutan.App.Controls;

/// <summary>
/// A row of filter chips (Search filters, Library tabs): exactly one chip is on.
///
/// A plain <see cref="ListBox"/> lets Ctrl+click or Ctrl+Space clear the selected
/// item, and with <c>AlwaysSelected</c> it then jumps to the first one, so the page
/// silently switched filters. Here any gesture on the chip that is already on does
/// nothing; picking another chip and the arrow keys work as in a ListBox.
/// </summary>
public sealed class ChipList : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    public override bool UpdateSelectionFromEvent(Control container, RoutedEventArgs eventArgs) =>
        container is ListBoxItem { IsSelected: true } || base.UpdateSelectionFromEvent(container, eventArgs);
}
