using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Layout;

namespace Rambutan.App.Views;

public partial class AlbumPage : UserControl
{
    /// <summary>A track row's item at its widest: the list's FixedWidth, the
    /// title's maximum, the liked mark, and the item's padding on both sides.
    /// The side column starts a gap past it.</summary>
    public const double RowsMaxWidth = 208 + TrackRowColumns.MaxTitle + TrackRowColumns.Liked + 16 + 16;

    /// <summary>The room kept at the pane's right edge for the list's scroll bar,
    /// as the hero keeps it.</summary>
    private const double RightInset = 16;

    private AlbumPageViewModel? _vm;
    private bool _onScreen;

    public AlbumPage()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
                _vm.HasSideRoom = false;
            }
            _vm = DataContext as AlbumPageViewModel;
            if (_vm is not null)
                _vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplySideColumn();
        };
        SizeChanged += (_, _) => ApplySideColumn();
        AttachedToVisualTree += (_, _) =>
        {
            _onScreen = true;
            ApplySideColumn();
        };
        // A page that left the screen has no column to fill: its view model
        // waits in the back stack, and a request still running is cancelled.
        DetachedFromVisualTree += (_, _) =>
        {
            _onScreen = false;
            if (_vm is not null)
                _vm.HasSideRoom = false;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlbumPageViewModel.ShowsSideColumn))
            ApplySideColumn();
    }

    /// <summary>Tells the view model whether the pane has room for the side
    /// column, and places the column past the rows' widest.</summary>
    private void ApplySideColumn()
    {
        if (!_onScreen || Bounds.Width <= 0)
            return;
        var fit = SideColumnFit.For(Bounds.Width - RightInset, RowsMaxWidth);
        if (_vm is not null)
            _vm.HasSideRoom = fit.Show;
        if (!fit.Show)
            return;
        SideColumn.Margin = new Thickness(fit.Main + SideColumnFit.Gap, 0, 0, 0);
        SideColumn.Width = fit.Side;
    }
}
