using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Rambutan.Core.Layout;

namespace Rambutan.App.Controls;

/// <summary>
/// A track list's optional album column (<see cref="TrackRowColumns"/>). The view
/// turns it on with the room its row template's fixed parts take; rows size
/// their album cell from the list and hide it at 0.
/// <code>
/// &lt;ListBox controls:TrackColumns.FixedWidth="318" … /&gt;
/// &lt;Panel Width="{Binding $parent[ListBox].(controls:TrackColumns.AlbumWidth)}"
///        IsVisible="{Binding $parent[ListBox].(controls:TrackColumns.ShowsAlbum)}" /&gt;
/// </code>
/// The width is the list's own, less its padding and border: the scroll bars
/// overlay the rows rather than take room from them.
///
/// A list that sets <see cref="HasAlbumProperty"/> or <see cref="HasLikedProperty"/>
/// lays its rows out by <see cref="TrackRowColumns.Fit"/> instead: its columns sit
/// at fixed positions and its rows stop at <see cref="RowWidthProperty"/>, which
/// each row's grid takes as its width (left aligned, title column <c>*</c>), so a
/// list's durations share one edge however long its titles are. There
/// <see cref="FixedWidthProperty"/> leaves out the item's padding
/// (<see cref="ItemInsetProperty"/>), the liked mark, the album with its gap and
/// the plays (<see cref="HasPlaysProperty"/>).
/// </summary>
public static class TrackColumns
{
    /// <summary>What the row's other columns, its gaps and the item padding take;
    /// 0 (the default) leaves the list without the column.</summary>
    public static readonly AttachedProperty<double> FixedWidthProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, double>("FixedWidth", typeof(TrackColumns));

    /// <summary>Written by this class only; 0 while the column doesn't show.</summary>
    public static readonly AttachedProperty<double> AlbumWidthProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, double>("AlbumWidth", typeof(TrackColumns));

    /// <summary>Whether <see cref="AlbumWidthProperty"/> is above 0, for bindings
    /// that show or hide by it.</summary>
    public static readonly AttachedProperty<bool> ShowsAlbumProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, bool>("ShowsAlbum", typeof(TrackColumns));

    /// <summary>The rows have an album column, laid out by <see cref="TrackRowColumns.Fit"/>.</summary>
    public static readonly AttachedProperty<bool> HasAlbumProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, bool>("HasAlbum", typeof(TrackColumns));

    /// <summary>The rows have a liked mark, laid out by <see cref="TrackRowColumns.Fit"/>.</summary>
    public static readonly AttachedProperty<bool> HasLikedProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, bool>("HasLiked", typeof(TrackColumns));

    /// <summary>The rows have a plays column, laid out by <see cref="TrackRowColumns.Fit"/>.</summary>
    public static readonly AttachedProperty<bool> HasPlaysProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, bool>("HasPlays", typeof(TrackColumns));

    /// <summary>Written by this class only: whether the plays column shows.</summary>
    public static readonly AttachedProperty<bool> ShowsPlaysProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, bool>("ShowsPlays", typeof(TrackColumns));

    /// <summary>The padding around each row's grid, both sides together (the
    /// item's, or the row border's in an ItemsControl).</summary>
    public static readonly AttachedProperty<double> ItemInsetProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, double>("ItemInset", typeof(TrackColumns));

    /// <summary>Written by this class only: whether the liked mark's slot shows.</summary>
    public static readonly AttachedProperty<bool> ShowsLikedProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, bool>("ShowsLiked", typeof(TrackColumns));

    /// <summary>Written by this class only: each row's width; NaN (auto, the row
    /// fills the item) until the list has a width, and in a list without the mode.</summary>
    public static readonly AttachedProperty<double> RowWidthProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, double>("RowWidth", typeof(TrackColumns), double.NaN);

    static TrackColumns()
    {
        FixedWidthProperty.Changed.AddClassHandler<ItemsControl>((list, _) => OnSettingChanged(list));
        HasAlbumProperty.Changed.AddClassHandler<ItemsControl>((list, _) => OnSettingChanged(list));
        HasLikedProperty.Changed.AddClassHandler<ItemsControl>((list, _) => OnSettingChanged(list));
        HasPlaysProperty.Changed.AddClassHandler<ItemsControl>((list, _) => OnSettingChanged(list));
        ItemInsetProperty.Changed.AddClassHandler<ItemsControl>((list, _) => OnSettingChanged(list));
        TemplatedControl.PaddingProperty.Changed.AddClassHandler<ItemsControl>((list, _) =>
        {
            if (IsActive(list))
                Update(list);
        });
    }

    public static double GetFixedWidth(ItemsControl list) => list.GetValue(FixedWidthProperty);

    public static void SetFixedWidth(ItemsControl list, double value) => list.SetValue(FixedWidthProperty, value);

    public static double GetAlbumWidth(ItemsControl list) => list.GetValue(AlbumWidthProperty);

    public static bool GetShowsAlbum(ItemsControl list) => list.GetValue(ShowsAlbumProperty);

    public static bool GetHasAlbum(ItemsControl list) => list.GetValue(HasAlbumProperty);

    public static void SetHasAlbum(ItemsControl list, bool value) => list.SetValue(HasAlbumProperty, value);

    public static bool GetHasLiked(ItemsControl list) => list.GetValue(HasLikedProperty);

    public static void SetHasLiked(ItemsControl list, bool value) => list.SetValue(HasLikedProperty, value);

    public static bool GetHasPlays(ItemsControl list) => list.GetValue(HasPlaysProperty);

    public static void SetHasPlays(ItemsControl list, bool value) => list.SetValue(HasPlaysProperty, value);

    public static bool GetShowsPlays(ItemsControl list) => list.GetValue(ShowsPlaysProperty);

    public static double GetItemInset(ItemsControl list) => list.GetValue(ItemInsetProperty);

    public static void SetItemInset(ItemsControl list, double value) => list.SetValue(ItemInsetProperty, value);

    public static bool GetShowsLiked(ItemsControl list) => list.GetValue(ShowsLikedProperty);

    public static double GetRowWidth(ItemsControl list) => list.GetValue(RowWidthProperty);

    /// <summary>Whether the list has the album column at all, whatever its width now.
    /// A list laid out by <see cref="TrackRowColumns.Fit"/> without an album has none.</summary>
    public static bool IsOn(ItemsControl list) => IsActive(list) && (!IsFitted(list) || GetHasAlbum(list));

    private static bool IsActive(ItemsControl list) => GetFixedWidth(list) > 0;

    private static bool IsFitted(ItemsControl list) => GetHasAlbum(list) || GetHasLiked(list);

    private static void OnSettingChanged(ItemsControl list)
    {
        list.SizeChanged -= OnSizeChanged;
        if (IsActive(list))
            list.SizeChanged += OnSizeChanged;
        Update(list);
    }

    private static void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is ItemsControl list)
            Update(list);
    }

    private static void Update(ItemsControl list)
    {
        var width = 0.0;
        var liked = false;
        var plays = false;
        var row = double.NaN;
        if (IsActive(list))
        {
            var padding = list.Padding;
            var border = list.BorderThickness;
            var rows = list.Bounds.Width - padding.Left - padding.Right - border.Left - border.Right;
            if (IsFitted(list))
            {
                var fit = TrackRowColumns.Fit(rows - GetItemInset(list), GetFixedWidth(list),
                    GetHasAlbum(list), GetHasLiked(list), GetHasPlays(list));
                width = fit.Album;
                liked = fit.ShowsLiked;
                plays = fit.ShowsPlays;
                row = fit.Content;
            }
            else
            {
                width = TrackRowColumns.AlbumWidth(rows, GetFixedWidth(list));
            }
        }

        // Each write lays every row out again, so only a change is written.
        // Equals, not ==, so NaN counts as unchanged.
        if (!width.Equals(list.GetValue(AlbumWidthProperty)))
            list.SetValue(AlbumWidthProperty, width);
        if ((width > 0) != list.GetValue(ShowsAlbumProperty))
            list.SetValue(ShowsAlbumProperty, width > 0);
        if (liked != list.GetValue(ShowsLikedProperty))
            list.SetValue(ShowsLikedProperty, liked);
        if (plays != list.GetValue(ShowsPlaysProperty))
            list.SetValue(ShowsPlaysProperty, plays);
        if (!row.Equals(list.GetValue(RowWidthProperty)))
            list.SetValue(RowWidthProperty, row);
    }
}
