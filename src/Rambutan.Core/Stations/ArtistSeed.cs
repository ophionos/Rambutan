namespace Rambutan.Core.Stations;

/// <summary>
/// An artist as YouTube Music names it, with some of its song titles (its top
/// songs, possibly none). A service that knows artists by its own ids uses the
/// titles to find the right one, since a bare name can name several.
/// </summary>
public sealed record ArtistSeed(string Name, IReadOnlyList<string> SongTitles);
