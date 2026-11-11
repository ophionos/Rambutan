using Avalonia;
using Avalonia.Controls.Primitives;

namespace Rambutan.App.Controls;

/// <summary>
/// The stats' summary tiles: cells of one width, in a single row while four fit,
/// else two by two, else one column, so no tile is narrower than
/// <see cref="TileWidth"/> and an odd one never sits alone on a row.
/// </summary>
public sealed class StatTileGrid : UniformGrid
{
    /// <summary>A tile's least width with the gap beside it.</summary>
    public const double TileWidth = 192;

    public static int ColumnsFor(double width) => width switch
    {
        _ when double.IsInfinity(width) || width >= 4 * TileWidth => 4,
        _ when width >= 2 * TileWidth => 2,
        _ => 1,
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = ColumnsFor(availableSize.Width);
        if (Columns != columns)
            Columns = columns;
        return base.MeasureOverride(availableSize);
    }
}
