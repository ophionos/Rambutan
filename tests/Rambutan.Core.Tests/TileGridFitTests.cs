using Rambutan.Core.Layout;

namespace Rambutan.Core.Tests;

public class TileGridFitTests
{
    private const double Min = 200;
    private const double Gap = 8;

    [Theory]
    // Wide: as many columns as fit, up to half the tiles, so both rows fill.
    [InlineData(12, 1300, 6, 12)]
    [InlineData(8, 1300, 4, 8)]
    // An odd count leaves the last tile out rather than a ragged row.
    [InlineData(7, 1300, 3, 6)]
    // Narrow: fewer columns, still two whole rows.
    [InlineData(12, 500, 2, 4)]
    [InlineData(4, 500, 2, 4)]
    // Narrower than one column: one column of two.
    [InlineData(12, 150, 1, 2)]
    public void Two_whole_rows_of_as_many_columns_as_fit(int count, double width, int columns, int shown)
    {
        var fit = TileGridFit.Fit(count, width, Min, Gap, rows: 2);

        Assert.Equal(columns, fit.Columns);
        Assert.Equal(shown, fit.Shown);
    }

    [Fact]
    public void Fewer_tiles_than_rows_fill_one_column()
    {
        Assert.Equal(new TileGridFit(1, 1), TileGridFit.Fit(1, 1300, Min, Gap, rows: 2));
        Assert.Equal(new TileGridFit(1, 0), TileGridFit.Fit(0, 1300, Min, Gap, rows: 2));
    }

    [Fact]
    public void An_unbounded_width_shows_every_tile_in_two_rows()
    {
        Assert.Equal(new TileGridFit(6, 12), TileGridFit.Fit(12, double.PositiveInfinity, Min, Gap, rows: 2));
    }
}
