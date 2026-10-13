namespace Rambutan.Core.Catalog;

/// <summary>
/// Translates list-index reorders into YTM's move-before edits
/// (<see cref="ICatalog.MovePlaylistItemAsync"/>), which name the row that will
/// follow the moved one rather than a position.
/// </summary>
public static class PlaylistMoves
{
    /// <summary>
    /// The setVideoId the row at <paramref name="from"/> must be moved before so it
    /// ends up at index <paramref name="to"/> (an index in the reordered list), or
    /// null when it ends up last. Throws for an index outside the list.
    /// </summary>
    public static string? Successor(IReadOnlyList<string> setVideoIds, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(setVideoIds);
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(from, setVideoIds.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(to);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(to, setVideoIds.Count);

        // In the reordered list, the row after index `to` is the original row at
        // `to` when moving up (everything from `to` shifts down), and at `to + 1`
        // when moving down (the moved row no longer sits before it).
        var next = to < from ? to : to + 1;
        return next < setVideoIds.Count ? setVideoIds[next] : null;
    }
}
