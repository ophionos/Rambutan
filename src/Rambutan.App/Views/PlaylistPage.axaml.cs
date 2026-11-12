using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Layout;

namespace Rambutan.App.Views;

public partial class PlaylistPage : UserControl
{
    /// <summary>A track row's item at its widest: the list's FixedWidth, the
    /// title's maximum, the liked mark, the album with its gap, and the item's
    /// padding. The side column starts a gap past it.</summary>
    public const double RowsMaxWidth =
        286 + TrackRowColumns.MaxTitle + TrackRowColumns.Liked + TrackRowColumns.AlbumGap + TrackRowColumns.MaxAlbum + 16;

    /// <summary>The room kept at the pane's right edge for the list's scroll bar,
    /// as the hero keeps it.</summary>
    private const double RightInset = 16;

    private PlaylistPageViewModel? _vm;

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

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm = DataContext as PlaylistPageViewModel;
            if (_vm is not null)
                _vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplySideColumn();
        };
        SizeChanged += (_, _) => ApplySideColumn();
        SideColumn.AddHandler(PointerWheelChangedEvent, OnSideColumnWheel);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistPageViewModel.ShowsSideColumn))
            ApplySideColumn();
    }

    /// <summary>Tells the view model whether the pane has room for the side
    /// column, and places the column past the rows' widest.</summary>
    private void ApplySideColumn()
    {
        if (Bounds.Width <= 0)
            return;
        var fit = SideColumnFit.For(Bounds.Width - RightInset, RowsMaxWidth);
        if (_vm is not null)
            _vm.HasSideRoom = fit.Show;
        if (!fit.Show)
            return;
        SideColumn.Margin = new Thickness(fit.Main + SideColumnFit.Gap, 0, 0, 0);
        SideColumn.Width = fit.Side;
    }

    // The column lies over the list, so the wheel over it would scroll nothing:
    // it scrolls the list, a wheel notch as far as the list's own (50 px a line).
    private void OnSideColumnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || e.Delta.Y == 0 || TrackList.Scroll is not ScrollViewer scroll)
            return;
        var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var y = Math.Clamp(scroll.Offset.Y - e.Delta.Y * 50, 0, max);
        scroll.Offset = scroll.Offset.WithY(y);
        e.Handled = true;
    }
}
