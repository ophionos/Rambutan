namespace Rambutan.Core.Layout;

/// <summary>
/// The columns of a grid of tiles a fixed number of rows tall (Home's leading
/// block): as many columns as fit the width, but no more than fill every row,
/// and only whole rows, so a narrow window shows fewer columns of the same
/// rows instead of a ragged or a third row.
/// </summary>
public readonly record struct TileGridFit(int Columns, int Shown)
{
    /// <param name="count">The tiles there are.</param>
    /// <param name="width">The grid's width; infinite lays every tile out.</param>
    /// <param name="minColumn">The narrowest a tile may be.</param>
    /// <param name="spacing">The gap between two columns.</param>
    /// <param name="rows">The rows the grid fills.</param>
    public static TileGridFit Fit(int count, double width, double minColumn, double spacing, int rows)
    {
        rows = Math.Max(1, rows);
        if (count < rows)
            return new TileGridFit(1, count);
        var filled = count / rows;
        var fit = double.IsFinite(width)
            ? (int)Math.Floor((width + spacing) / (minColumn + spacing))
            : filled;
        var columns = Math.Clamp(fit, 1, filled);
        return new TileGridFit(columns, columns * rows);
    }
}
