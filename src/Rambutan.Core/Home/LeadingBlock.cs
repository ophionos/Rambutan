using Rambutan.Core.Models;
using Rambutan.Core.Stats;

namespace Rambutan.Core.Home;

/// <summary>
/// The tiles above Home's shelves, of what to go back to (the maintainer's
/// rule): YouTube Music's own Listen again or Quick picks shelf when Home's
/// first page has one, taken out of the shelves so it never shows twice;
/// else the albums of the play log's latest plays. The play log keeps no
/// playlist and no link for a local file, so it gives albums only.
/// </summary>
/// <param name="ShelfTitle">YouTube Music's title for the shelf the tiles came
/// from; null when they came from the play log.</param>
public sealed record LeadingBlock(IReadOnlyList<CatalogEntry> Tiles, string? ShelfTitle)
{
    /// <summary>Two rows of the widest grid Home lays out.</summary>
    public const int MaxTiles = 12;

    /// <summary>Fewer than this make no block: two rows of two at the narrowest.</summary>
    public const int MinTiles = 4;

    // InnerTube asks in English whatever the UI language, and the fixtures show
    // no mark in the response that sets these shelves apart from other song
    // lists, so the title is what names them.
    private static readonly string[] RevisitTitles = ["Listen again", "Quick picks"];

    public static bool IsRevisitTitle(string title) =>
        RevisitTitles.Any(t => string.Equals(t, title.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The block from the first <see cref="ShelfKind.Revisit"/> shelf with
    /// at least <see cref="MinTiles"/> entries, and the shelves without it; else
    /// no block and the shelves as they came.</summary>
    public static (LeadingBlock? Block, IReadOnlyList<Shelf> Shelves) FromHome(IReadOnlyList<Shelf> shelves)
    {
        var source = shelves.FirstOrDefault(s => s.Kind == ShelfKind.Revisit && s.Entries.Count >= MinTiles);
        if (source is null)
            return (null, shelves);
        var block = new LeadingBlock([.. source.Entries.Take(MaxTiles)], source.Title);
        return (block, [.. shelves.Where(s => !ReferenceEquals(s, source))]);
    }

    /// <summary>The albums of the latest plays that count
    /// (<see cref="PlayRules"/>), newest first, once each; only YouTube plays
    /// whose album has a page. Null when fewer than <see cref="MinTiles"/>.</summary>
    public static LeadingBlock? FromPlays(IEnumerable<PlayRecord> plays)
    {
        var albums = plays
            .Where(p => PlayRules.CountsAsPlay(p) && !PlayRules.IsTimeOnly(p) &&
                        YouTubeIds.IsVideoId(p.VideoId) &&
                        !string.IsNullOrWhiteSpace(p.AlbumBrowseId) && !string.IsNullOrWhiteSpace(p.Album))
            .GroupBy(p => p.AlbumBrowseId!)
            .Select(album =>
            {
                var newestFirst = album.OrderByDescending(p => p.StartedAt).ToList();
                var latest = newestFirst[0];
                return (latest.StartedAt, Entry: (CatalogEntry)new AlbumEntry
                {
                    Title = latest.Album!,
                    Subtitle = latest.Artists.FirstOrDefault()?.Name,
                    ThumbnailUrl = newestFirst.FirstOrDefault(p => p.ThumbnailUrl is not null)?.ThumbnailUrl,
                    BrowseId = album.Key,
                });
            })
            .OrderByDescending(a => a.StartedAt)
            .Take(MaxTiles)
            .Select(a => a.Entry)
            .ToList();
        return albums.Count >= MinTiles ? new LeadingBlock(albums, null) : null;
    }
}
