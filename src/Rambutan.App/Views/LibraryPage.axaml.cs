using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class LibraryPage : UserControl
{
    private LibraryPageViewModel? _vm;

    public LibraryPage()
    {
        InitializeComponent();

        // Continuation-driven paging as on playlist pages. Extent
        // changes raise this too, so a first page too short to scroll keeps
        // loading until it fills the view. A failed page waits for its Try again
        // instead of re-firing on every scroll tick.
        Scroller.ScrollChanged += (_, _) =>
        {
            if (_vm?.SelectedSection is { HasMore: true, IsLoadingMore: false, LoadError: null } section &&
                ScrollPaging.IsNearEnd(Scroller.Offset.Y, Scroller.Viewport.Height, Scroller.Extent.Height))
            {
                section.LoadMoreCommand.Execute(null);
            }
        };
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as LibraryPageViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelChanged;
    }

    // Each tab starts at its top; the scroller is shared between them.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryPageViewModel.SelectedSection))
            Scroller.Offset = new Vector(Scroller.Offset.X, 0);
    }
}
