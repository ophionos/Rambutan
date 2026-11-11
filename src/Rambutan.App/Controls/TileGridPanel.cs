using System;
using Avalonia;
using Avalonia.Controls;
using Rambutan.Core.Layout;

namespace Rambutan.App.Controls;

/// <summary>
/// <see cref="Rows"/> whole rows of equal tiles, with as many columns as the
/// width holds (<see cref="TileGridFit"/>). The tiles that don't fit are
/// hidden, not just left unarranged, so the arrow keys and Tab never reach a
/// tile that isn't on screen.
/// </summary>
public class TileGridPanel : Panel
{
    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<TileGridPanel, int>(nameof(Rows), 2);

    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<TileGridPanel, double>(nameof(MinColumnWidth), 200);

    /// <summary>The widest a tile grows; a wide window with few tiles leaves
    /// the rest of the width empty rather than stretch them.</summary>
    public static readonly StyledProperty<double> MaxColumnWidthProperty =
        AvaloniaProperty.Register<TileGridPanel, double>(nameof(MaxColumnWidth), double.PositiveInfinity);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<TileGridPanel, double>(nameof(Spacing), 8);

    static TileGridPanel() =>
        AffectsMeasure<TileGridPanel>(RowsProperty, MinColumnWidthProperty, MaxColumnWidthProperty, SpacingProperty);

    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double MaxColumnWidth
    {
        get => GetValue(MaxColumnWidthProperty);
        set => SetValue(MaxColumnWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private TileGridFit _fit;
    private double _columnWidth;
    private double _rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        _fit = TileGridFit.Fit(Children.Count, availableSize.Width, MinColumnWidth, Spacing, Rows);
        var columns = Math.Max(1, _fit.Columns);
        var room = double.IsFinite(availableSize.Width)
            ? (availableSize.Width - Spacing * (columns - 1)) / columns
            : MinColumnWidth;
        _columnWidth = Math.Max(0, Math.Min(MaxColumnWidth, room));

        _rowHeight = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            // Hiding a child measures the panel again, which then finds nothing to change.
            var shown = i < _fit.Shown;
            if (child.IsVisible != shown)
                child.IsVisible = shown;
            child.Measure(new Size(_columnWidth, double.PositiveInfinity));
            if (shown)
                _rowHeight = Math.Max(_rowHeight, child.DesiredSize.Height);
        }

        var rows = (_fit.Shown + columns - 1) / columns;
        return rows == 0
            ? default
            : new Size(
                columns * _columnWidth + Spacing * (columns - 1),
                rows * _rowHeight + Spacing * (rows - 1));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Math.Max(1, _fit.Columns);
        for (var i = 0; i < Children.Count; i++)
        {
            if (i >= _fit.Shown)
            {
                Children[i].Arrange(default);
                continue;
            }
            int column = i % columns, row = i / columns;
            Children[i].Arrange(new Rect(
                column * (_columnWidth + Spacing), row * (_rowHeight + Spacing), _columnWidth, _rowHeight));
        }
        return finalSize;
    }
}
