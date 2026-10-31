namespace Rambutan.Core.Import;

/// <summary>A song as another service's file listed it, before it is matched
/// to a YouTube Music song.</summary>
/// <param name="Artists">The artists as listed, the first one first; empty
/// when the file named none.</param>
/// <param name="Duration">Null when the file gave no length.</param>
/// <param name="SpotifyId">The Spotify track id (22 characters), when the
/// file gave one; null for a file the user added to Spotify from their disk.</param>
/// <param name="Isrc">The recording's ISRC, when the file gave one.</param>
public sealed record ImportedTrack(
    string Title,
    IReadOnlyList<string> Artists,
    string? Album = null,
    TimeSpan? Duration = null,
    string? SpotifyId = null,
    string? Isrc = null);

public enum ImportedListKind
{
    Playlist,

    /// <summary>The account's liked songs (Spotify's Liked Songs): it has no
    /// name of its own in the file, so the app names it.</summary>
    LikedSongs,
}

/// <summary>A list read from an import file.</summary>
/// <param name="Name">The list's name; empty for <see cref="ImportedListKind.LikedSongs"/>.</param>
/// <param name="Tracks">The songs in the file's order, duplicates included.</param>
/// <param name="Skipped">Rows that aren't songs (podcast episodes, audiobook
/// chapters) or have no title, which the import leaves out and says so.</param>
/// <param name="Source">The list's Spotify URI (<see cref="SpotifyListLink.Uri"/>)
/// when it was read from a link, so it can be read again; null for a file's list.</param>
/// <param name="Incomplete">The read stopped before the list's end: only its
/// first songs are here.</param>
public sealed record ImportedList(
    ImportedListKind Kind,
    string Name,
    IReadOnlyList<ImportedTrack> Tracks,
    int Skipped = 0,
    string? Source = null,
    bool Incomplete = false);

public enum ImportProblem
{
    /// <summary>Not a file this import reads: not JSON from Spotify's data
    /// export, or not a CSV with a header line.</summary>
    Unreadable,

    /// <summary>A CSV with no column this import reads as the songs' titles.</summary>
    NoTitleColumn,
}

/// <summary>Thrown when an import file can't be read; <see cref="Problem"/>
/// says why, for the import to show.</summary>
public sealed class ImportFormatException(ImportProblem problem, string message, Exception? inner = null)
    : FormatException(message, inner)
{
    public ImportProblem Problem { get; } = problem;
}
