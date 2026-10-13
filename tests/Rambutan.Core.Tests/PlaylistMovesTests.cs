using Rambutan.Core.Catalog;

namespace Rambutan.Core.Tests;

public class PlaylistMovesTests
{
    private static readonly string[] Rows = ["a", "b", "c", "d"];

    [Theory]
    [InlineData(3, 0, "a")]    // last to first: before the old first
    [InlineData(2, 1, "b")]    // up one
    [InlineData(0, 1, "c")]    // down one: before the row after its new neighbour
    [InlineData(0, 3, null)]   // first to last: to the end
    [InlineData(1, 3, null)]
    [InlineData(1, 2, "d")]
    [InlineData(2, 2, "d")]    // no move: before its own successor
    [InlineData(3, 3, null)]
    public void Successor_IsTheRowThatFollowsInTheNewOrder(int from, int to, string? expected)
    {
        Assert.Equal(expected, PlaylistMoves.Successor(Rows, from, to));
    }

    [Fact]
    public void Successor_AgreesWithSimulatingTheMove()
    {
        for (var from = 0; from < Rows.Length; from++)
        {
            for (var to = 0; to < Rows.Length; to++)
            {
                var reordered = Rows.ToList();
                var moved = reordered[from];
                reordered.RemoveAt(from);
                reordered.Insert(to, moved);
                var expected = to + 1 < reordered.Count ? reordered[to + 1] : null;

                Assert.Equal(expected, PlaylistMoves.Successor(Rows, from, to));
            }
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(4, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 4)]
    public void Successor_OutOfRange_Throws(int from, int to)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaylistMoves.Successor(Rows, from, to));
    }
}
