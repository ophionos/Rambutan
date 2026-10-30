using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Rambutan.App.Controls;

/// <summary>
/// Equal cells in rows of <see cref="Columns"/>, for lists laid out as a chart or
/// a table (the stats' bars and heatmap). A list moves through it with all four
/// arrow keys: Up and Down go a row. Each cell's edges are rounded on their own,
/// so the cells fill the width exactly: rounding each cell's width instead, as a
/// uniform grid does, pushes 24 cells several pixels past the edge, or leaves a
/// year's 365 bars well short of it.
/// </summary>
public class GridNavigationPanel : Panel, INavigableContainer
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<GridNavigationPanel, int>(nameof(Columns), 1);

    static GridNavigationPanel() => AffectsMeasure<GridNavigationPanel>(ColumnsProperty);

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    private int ColumnCount => Math.Max(1, Columns);

    private int RowCount => Math.Max(1, (Children.Count + ColumnCount - 1) / ColumnCount);

    protected override Size MeasureOverride(Size availableSize)
    {
        var cell = new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width / ColumnCount : double.PositiveInfinity,
            double.IsFinite(availableSize.Height) ? availableSize.Height / RowCount : double.PositiveInfinity);
        double widest = 0, tallest = 0;
        foreach (var child in Children)
        {
            child.Measure(cell);
            widest = Math.Max(widest, child.DesiredSize.Width);
            tallest = Math.Max(tallest, child.DesiredSize.Height);
        }
        return new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : widest * ColumnCount,
            tallest * RowCount);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnCount;
        var rows = RowCount;
        for (var i = 0; i < Children.Count; i++)
        {
            int column = i % columns, row = i / columns;
            var left = Math.Round(column * finalSize.Width / columns);
            var right = Math.Round((column + 1) * finalSize.Width / columns);
            var top = Math.Round(row * finalSize.Height / rows);
            var bottom = Math.Round((row + 1) * finalSize.Height / rows);
            Children[i].Arrange(new Rect(left, top, right - left, bottom - top));
        }
        return finalSize;
    }

    IInputElement? INavigableContainer.GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var count = Children.Count;
        if (count == 0)
            return null;
        var columns = ColumnCount;
        var index = from is Control control ? Children.IndexOf(control) : -1;

        var next = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            _ when index < 0 => 0,
            NavigationDirection.Left or NavigationDirection.Previous => index % columns > 0 ? index - 1 : -1,
            NavigationDirection.Right or NavigationDirection.Next => index % columns < columns - 1 && index + 1 < count ? index + 1 : -1,
            NavigationDirection.Up => index - columns,
            NavigationDirection.Down => index + columns,
            _ => -1,
        };
        return next >= 0 && next < count ? Children[next] : null;
    }
}
