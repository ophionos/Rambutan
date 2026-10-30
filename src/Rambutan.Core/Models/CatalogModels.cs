using Rambutan.Core.Catalog;

namespace Rambutan.Core.Models;

public sealed record ContinuationToken(string Value);

public abstract record CatalogEntry
{
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? ThumbnailUrl { get; init; }
}

public sealed record SongEntry : CatalogEntry
{
    public required TrackInfo Track { get; init; }
}

/// <summary>One song in the account's YouTube Music history.</summary>
public sealed record HistoryEntry : CatalogEntry
{
    public required TrackInfo Track { get; init; }
    /// <summary>YouTube Music's own heading for when it was played ("Today",
    /// "Last week", a month), in the account's language.</summary>
    public required string Played { get; init; }
    /// <summary>What <see cref="ICatalog.RemoveFromHistoryAsync"/> sends;
    /// null when the row's menu doesn't offer the removal.</summary>
    public string? RemoveToken { get; init; }
}

public sealed record AlbumEntry : CatalogEntry
{
    public required string BrowseId { get; init; }
    /// <summary>
    /// The album's own playlist (OLAK5uy_…), which is what saving it to the library
    /// targets. Null where the surface doesn't carry it.
    /// </summary>
    public string? AudioPlaylistId { get; init; }
}

public sealed record PlaylistEntry : CatalogEntry
{
    public required string PlaylistId { get; init; }
    /// <summary>
    /// True for the signed-in user's own playlist, which is deleted rather than
    /// taken out of the library; false for someone else's. Null where the card
    /// doesn't say: auto playlists, and surfaces whose cards carry no menu.
    /// </summary>
    public bool? IsOwn { get; init; }
}

public sealed record ArtistEntry : CatalogEntry
{
    public required string BrowseId { get; init; }
}

public sealed record Shelf(string Title, IReadOnlyList<CatalogEntry> Entries)
{
    /// <summary>The mood or genre page behind the shelf's "More", where YTM links one.</summary>
    public MoodCategory? More { get; init; }
}

/// <summary>
/// One page of Home's shelves. YTM sends a few shelves at a time, and the web
/// client asks for more as the user scrolls; <see cref="Continuation"/> fetches
/// the next few, and is null on the last page.
/// </summary>
public sealed record HomePage
{
    public required IReadOnlyList<Shelf> Shelves { get; init; }
    public ContinuationToken? Continuation { get; init; }

    /// <summary>
    /// The mood chips above Home's first page (Relax, Energize…), each of which
    /// fetches Home again filtered to it. Empty on later pages.
    /// </summary>
    public IReadOnlyList<HomeChip> Chips { get; init; } = [];

    public static HomePage Empty { get; } = new() { Shelves = [] };
}

public sealed record HomeChip(string Title, string Params, bool IsSelected);

/// <summary>
/// A tile on the Moods &amp; genres page, or a page linked from a shelf's "More":
/// <see cref="Params"/> opens it with <see cref="ICatalog.GetMoodCategoryAsync"/>.
/// </summary>
public sealed record MoodCategory(string Title, string Params)
{
    /// <summary>The tile's accent colour as YTM sends it (0xAARRGGBB), if any.</summary>
    public uint? Color { get; init; }
}

/// <summary>A titled group of tiles: "Moods &amp; moments", "Genres".</summary>
public sealed record MoodSection(string Title, IReadOnlyList<MoodCategory> Categories);

public sealed record MoodsPage
{
    public required IReadOnlyList<MoodSection> Sections { get; init; }

    public static MoodsPage Empty { get; } = new() { Sections = [] };
}

/// <summary>One mood or genre: its shelves of playlists, and for a genre its songs, videos and albums.</summary>
public sealed record MoodCategoryPage
{
    public string? Title { get; init; }
    public required IReadOnlyList<Shelf> Shelves { get; init; }
}

/// <summary>
/// One page of a filtered search. <see cref="Continuation"/> fetches
/// the next page with the same query and filter; null on the last page.
/// </summary>
public sealed record SearchPage
{
    public required IReadOnlyList<CatalogEntry> Items { get; init; }
    public ContinuationToken? Continuation { get; init; }

    public static SearchPage Empty { get; } = new() { Items = [] };
}

/// <summary>
/// An unfiltered search (the "All" tab): YTM's top-result card, then
/// its results in the server's order, with the types mixed (albums, songs, artists,
/// videos, playlists, profiles). YTM sends no per-type shelves here.
/// </summary>
public sealed record SearchOverview
{
    public TopResult? TopResult { get; init; }
    public required IReadOnlyList<CatalogEntry> Results { get; init; }

    public static SearchOverview Empty { get; } = new() { Results = [] };
}

/// <summary>
/// The card YTM puts above an unfiltered search: the entity it thinks was meant
/// (an artist, album, song, video or playlist; <see cref="CatalogEntry.Subtitle"/>
/// is the card's line, e.g. "Artist • 113M monthly audience"), plus the rows shown
/// inside the card, such as that artist's top songs.
/// </summary>
public sealed record TopResult
{
    public required CatalogEntry Entry { get; init; }
    public required IReadOnlyList<CatalogEntry> Related { get; init; }
}

/// <summary>
/// One query completion under the search box. <see cref="FromHistory"/> marks the
/// signed-in user's own past searches, which YTM lists first.
/// </summary>
public sealed record SearchSuggestion(string Query, bool FromHistory = false);

/// <summary>
/// What YTM offers while typing: query completions, then any direct
/// entities (an artist, song or album) it matched outright.
/// </summary>
public sealed record SearchSuggestions
{
    public required IReadOnlyList<SearchSuggestion> Queries { get; init; }
    public required IReadOnlyList<CatalogEntry> Entries { get; init; }

    public static SearchSuggestions Empty { get; } = new() { Queries = [], Entries = [] };
}

public sealed record AlbumPage
{
    public required string BrowseId { get; init; }
    public required string Title { get; init; }
    public string? Artist { get; init; }
    public string? ArtistBrowseId { get; init; }
    public string? SubtitleInfo { get; init; }     // "Album • 2013"
    public string? SecondSubtitle { get; init; }   // "13 songs • 1 hour, 14 minutes"
    public string? ThumbnailUrl { get; init; }
    /// <summary>The album's own playlist (OLAK5uy_…); saving it to the library targets this.</summary>
    public string? AudioPlaylistId { get; init; }
    /// <summary>Whether the album is in the signed-in user's library; null when signed out or unreported.</summary>
    public bool? InLibrary { get; init; }
    public required IReadOnlyList<TrackInfo> Tracks { get; init; }
    /// <summary>Set when this is the copy kept with the downloads, shown because the
    /// catalog couldn't be reached: when that copy was made. Null for a live page.</summary>
    public DateTimeOffset? KeptAt { get; init; }
}

public sealed record PlaylistPage
{
    public required string PlaylistId { get; init; }
    public required string Title { get; init; }
    public string? SubtitleInfo { get; init; }
    public string? SecondSubtitle { get; init; }
    /// <summary>
    /// Who made the playlist (a user channel, an artist, or "YouTube Music" with no
    /// link). Null for collaborative playlists, whose header lists collaborators instead.
    /// </summary>
    public ArtistRef? Author { get; init; }
    /// <summary>The year in the header ("Playlist • 2024"), when there is one.</summary>
    public int? Year { get; init; }
    /// <summary>The header's track count ("101 songs"): the whole playlist, not the loaded page.</summary>
    public int? TrackCount { get; init; }
    /// <summary>The header's total length as YTM words it ("7+ hours"); not parsed, since it's rounded.</summary>
    public string? DurationText { get; init; }
    public string? Description { get; init; }
    public string? ThumbnailUrl { get; init; }
    public required IReadOnlyList<TrackInfo> Tracks { get; init; }
    public ContinuationToken? Continuation { get; init; }
    /// <summary>The account owns the playlist, so its rows can be moved. False when
    /// the page doesn't say, so an edit is never offered on a playlist that refuses it.</summary>
    public bool IsEditable { get; init; }
    /// <summary>Set when this is the copy kept with the downloads, shown because the
    /// catalog couldn't be reached: when that copy was made. Null for a live page.</summary>
    public DateTimeOffset? KeptAt { get; init; }
}

public sealed record LibraryPage
{
    public required IReadOnlyList<CatalogEntry> Items { get; init; }
    public ContinuationToken? Continuation { get; init; }
    /// <summary>The library couldn't be reached, so these are only the albums or
    /// playlists kept with the downloads.</summary>
    public bool IsOfflineCopy { get; init; }
}

public sealed record ArtistPage
{
    public required string BrowseId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ThumbnailUrl { get; init; }
    /// <summary>Whether the signed-in user subscribes to the artist; null when signed out or unreported.</summary>
    public bool? Subscribed { get; init; }
    public required IReadOnlyList<TrackInfo> TopSongs { get; init; }
    public required IReadOnlyList<Shelf> Shelves { get; init; }
    /// <summary>The artist's radio, from the header's Mix button; null when the
    /// header has none.</summary>
    public MixSeed? Mix { get; init; }
    /// <summary>A mix of the artist's own songs, from the header's Shuffle button
    /// (<c>RDAO…</c>); null when the header has none.</summary>
    public MixSeed? Shuffle { get; init; }
}

/// <summary>
/// Where a YouTube Music mix starts: a first track and the mix's playlist, as a
/// page's Mix button names them (an artist's radio is <c>RDEM…</c>, the shuffle
/// of its songs <c>RDAO…</c>).
/// <paramref name="Params"/> is the button's own request flag, passed on as it came.
/// </summary>
public sealed record MixSeed(string VideoId, string PlaylistId, string? Params = null);
