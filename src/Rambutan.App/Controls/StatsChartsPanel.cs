using System;
using Avalonia;
using Avalonia.Controls;
using Rambutan.Core.Layout;

namespace Rambutan.App.Controls;

/// <summary>
/// The stats' two charts: side by side, each half the width, when
/// <see cref="StatsChartsFit"/> says the width holds both, else one under the
/// other. The children keep their order either way, so the tab order doesn't
/// change with the width.
/// </summary>
public sealed class StatsChartsPanel : Panel
{
    /// <summary>Whether the last layout put the children side by side.</summary>
    public bool IsSideBySide { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        IsSideBySide = StatsChartsFit.SideBySide(availableSize.Width);
        if (IsSideBySide)
        {
            var half = new Size(StatsChartsFit.Half(availableSize.Width), availableSize.Height);
            var height = 0d;
            foreach (var child in Children)
            {
                child.Measure(half);
                height = Math.Max(height, child.DesiredSize.Height);
            }
            return new Size(availableSize.Width, height);
        }

        var width = 0d;
        var total = 0d;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
            total += child.DesiredSize.Height;
        }
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (IsSideBySide)
        {
            var half = StatsChartsFit.Half(finalSize.Width);
            var x = 0d;
            foreach (var child in Children)
            {
                // Each at its own height from the top: the shorter chart's
                // line under it stays with it.
                child.Arrange(new Rect(x, 0, half, child.DesiredSize.Height));
                x += half + StatsChartsFit.Gap;
            }
            return finalSize;
        }

        var y = 0d;
        foreach (var child in Children)
        {
            child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
            y += child.DesiredSize.Height;
        }
        return finalSize;
    }
}
