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
/// </summary>
public static class TrackColumns
{
    /// <summary>What the row's other columns, its gaps and the item padding take;
    /// 0 (the default) leaves the list without the column.</summary>
    public static readonly AttachedProperty<double> FixedWidthProperty =
        AvaloniaProperty.RegisterAttached<ListBox, double>("FixedWidth", typeof(TrackColumns));

    /// <summary>Written by this class only; 0 while the column doesn't show.</summary>
    public static readonly AttachedProperty<double> AlbumWidthProperty =
        AvaloniaProperty.RegisterAttached<ListBox, double>("AlbumWidth", typeof(TrackColumns));

    /// <summary>Whether <see cref="AlbumWidthProperty"/> is above 0, for bindings
    /// that show or hide by it.</summary>
    public static readonly AttachedProperty<bool> ShowsAlbumProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("ShowsAlbum", typeof(TrackColumns));

    static TrackColumns()
    {
        FixedWidthProperty.Changed.AddClassHandler<ListBox>((list, e) =>
        {
            list.SizeChanged -= OnSizeChanged;
            if (IsOn(list))
                list.SizeChanged += OnSizeChanged;
            Update(list);
        });
        TemplatedControl.PaddingProperty.Changed.AddClassHandler<ListBox>((list, _) =>
        {
            if (IsOn(list))
                Update(list);
        });
    }

    public static double GetFixedWidth(ListBox list) => list.GetValue(FixedWidthProperty);

    public static void SetFixedWidth(ListBox list, double value) => list.SetValue(FixedWidthProperty, value);

    public static double GetAlbumWidth(ListBox list) => list.GetValue(AlbumWidthProperty);

    public static bool GetShowsAlbum(ListBox list) => list.GetValue(ShowsAlbumProperty);

    /// <summary>Whether the list has the column at all, whatever its width now.</summary>
    public static bool IsOn(ListBox list) => GetFixedWidth(list) > 0;

    private static void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is ListBox list)
            Update(list);
    }

    private static void Update(ListBox list)
    {
        var width = 0.0;
        if (IsOn(list))
        {
            var padding = list.Padding;
            var border = list.BorderThickness;
            var rows = list.Bounds.Width - padding.Left - padding.Right - border.Left - border.Right;
            width = TrackRowColumns.AlbumWidth(rows, GetFixedWidth(list));
        }

        // Each write lays every row out again, so only a change is written.
        if (width != list.GetValue(AlbumWidthProperty))
            list.SetValue(AlbumWidthProperty, width);
        if ((width > 0) != list.GetValue(ShowsAlbumProperty))
            list.SetValue(ShowsAlbumProperty, width > 0);
    }
}
