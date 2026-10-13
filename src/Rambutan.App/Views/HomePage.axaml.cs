using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Views;

public partial class HomePage : UserControl
{
    // The chip a keyboard press came from, until the chips it brings are shown.
    private string? _refocusChip;

    public HomePage()
    {
        InitializeComponent();

        // YTM sends Home a few shelves at a time: ask for more near the end. This
        // also fires when shelves arrive (the extent changes), so a window taller
        // than the shelves keeps asking until it's full or none are left.
        Scroller.ScrollChanged += (_, _) =>
        {
            if (DataContext is HomePageViewModel { HasMore: true, MoreError: null } vm &&
                ScrollPaging.IsNearEnd(Scroller.Offset.Y, Scroller.Viewport.Height, Scroller.Extent.Height))
            {
                vm.LoadMoreCommand.Execute(null);
            }
        };

        // A chip's page sends every chip again, so the pressed one's button is
        // replaced and focus would drop to nothing. Put it back on the chip of the
        // same name (the order can change), unless focus has moved on meanwhile.
        ChipRow.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (ChipRow.IsKeyboardFocusWithin && e.Source is Button { DataContext: HomeChip chip })
                _refocusChip = chip.Title;
        });
        ChipRow.ContainerPrepared += (_, e) =>
        {
            if (_refocusChip is not { } title || e.Container.DataContext is not HomeChip chip || chip.Title != title)
                return;
            _refocusChip = null;
            Dispatcher.UIThread.Post(() =>
            {
                var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
                if (focused is not null && !ChipRow.IsVisualAncestorOf(focused))
                    return;
                e.Container.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Loaded);
        };
    }
}
