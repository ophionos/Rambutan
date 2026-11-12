using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Rambutan.Core.Layout;

namespace Rambutan.App.Controls;

/// <summary>
/// The Settings sections: a stack while <see cref="Columns"/> is 1, else two
/// columns of equal width, filled in order down the first and then the second,
/// split where <see cref="SettingsColumnFit.Split"/> puts it. The children keep
/// their order, so the tab order follows the sections' order.
/// </summary>
public sealed class SettingsColumnsPanel : StackPanel
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<SettingsColumnsPanel, int>(nameof(Columns), 1);

    public static readonly StyledProperty<double> ColumnGapProperty =
        AvaloniaProperty.Register<SettingsColumnsPanel, double>(nameof(ColumnGap), SettingsColumnFit.ColumnGap);

    private readonly List<double> _heights = [];
    private bool _measuredInColumns;

    static SettingsColumnsPanel() => AffectsMeasure<SettingsColumnsPanel>(ColumnsProperty, ColumnGapProperty);

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public double ColumnGap
    {
        get => GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    /// <summary>How many children the last layout put in the first column;
    /// all of them in one column.</summary>
    public int FirstColumnCount { get; private set; }

    /// <summary>Styled as the stack it stands in for.</summary>
    protected override Type StyleKeyOverride => typeof(StackPanel);

    private bool InColumns => Columns >= 2 && Orientation == Avalonia.Layout.Orientation.Vertical;

    private double ColumnWidth(double width) => Math.Max(0, (width - ColumnGap) / 2);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!InColumns || double.IsInfinity(availableSize.Width))
        {
            FirstColumnCount = Children.Count;
            _measuredInColumns = false;
            return base.MeasureOverride(availableSize);
        }

        _measuredInColumns = true;
        var column = new Size(ColumnWidth(availableSize.Width), double.PositiveInfinity);
        _heights.Clear();
        foreach (var child in Children)
        {
            child.Measure(column);
            _heights.Add(child.IsVisible ? child.DesiredSize.Height + Spacing : 0);
        }
        FirstColumnCount = SettingsColumnFit.Split(_heights);

        double first = 0, second = 0;
        for (var i = 0; i < _heights.Count; i++)
        {
            if (i < FirstColumnCount)
                first += _heights[i];
            else
                second += _heights[i];
        }
        // Each column's last section has no space under it.
        var height = Math.Max(0, Math.Max(first, second) - Spacing);
        return new Size(availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_measuredInColumns)
            return base.ArrangeOverride(finalSize);

        var width = ColumnWidth(finalSize.Width);
        double x = 0, y = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            if (i == FirstColumnCount)
            {
                x = width + ColumnGap;
                y = 0;
            }
            var child = Children[i];
            if (!child.IsVisible)
                continue;
            child.Arrange(new Rect(x, y, width, child.DesiredSize.Height));
            y += child.DesiredSize.Height + Spacing;
        }
        return finalSize;
    }
}
