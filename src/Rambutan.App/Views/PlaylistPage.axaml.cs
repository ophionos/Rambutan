using Avalonia.Controls;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class PlaylistPage : UserControl
{
    public PlaylistPage()
    {
        InitializeComponent();

        // Continuation-driven infinite scroll: fetch when the list nears its end.
        // Extent changes raise this too, so a first page too short to scroll
        // keeps loading. A failed page waits for its Try again instead of
        // re-firing on every scroll tick, as in the Library.
        TrackList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) =>
        {
            if (DataContext is not PlaylistPageViewModel { HasMore: true, LoadError: null } vm ||
                TrackList.Scroll is not ScrollViewer scroll)
            {
                return;
            }

            if (ScrollPaging.IsNearEnd(scroll.Offset.Y, scroll.Viewport.Height, scroll.Extent.Height))
                vm.LoadMoreCommand.Execute(null);
        });
    }
}
