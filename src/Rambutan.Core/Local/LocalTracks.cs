using Rambutan.Core.Models;

namespace Rambutan.Core.Local;

/// <summary>
/// Local files as the rest of the app sees them: a <see cref="TrackInfo"/> whose
/// <see cref="TrackInfo.VideoId"/> is the file's <c>local:</c> id, so the queue,
/// the player and the integrations take it like any other track.
/// </summary>
public static class LocalTracks
{
    /// <summary>
    /// The track for an indexed file. The title falls back to the file's name, so
    /// a file without tags still reads as something. There is no thumbnail URL:
    /// the cover is looked up by id (it is a file in the profile's folder, which
    /// can move), and a URL saved with the queue would pin the old place.
    /// </summary>
    public static TrackInfo ToTrack(LocalFile file, LocalPaths? paths = null)
    {
        var tags = file.Tags;
        var artist = tags?.Artist ?? tags?.AlbumArtist;
        return new TrackInfo
        {
            VideoId = file.Id,
            Title = tags?.Title ?? (paths ?? LocalPaths.Platform).NameWithoutExtension(file.Path),
            Artists = artist is null ? [] : [new ArtistRef(artist)],
            Album = tags?.Album,
            Duration = tags?.Duration,
        };
    }

    /// <summary>
    /// ReplayGain's track gain as the loudness that volume normalization takes:
    /// YouTube's, in dB above its reference of -14 LUFS. ReplayGain 2.0 gives the
    /// gain that brings the track to -18 LUFS, so the track is at -18 - gain LUFS,
    /// which is -4 - gain above YouTube's reference. Null without a track gain.
    /// </summary>
    public static double? LoudnessDb(LocalFileTags? tags) =>
        tags?.TrackGainDb is { } gain ? -4 - gain : null;

    /// <summary>
    /// The order the local library lists files in: by album artist (else artist),
    /// album, disc and track number, then title, ignoring case. Files without a
    /// tag go after those with one at each level.
    /// </summary>
    public static IReadOnlyList<LocalFile> InLibraryOrder(IEnumerable<LocalFile> files, LocalPaths? paths = null)
    {
        paths ??= LocalPaths.Platform;
        var text = StringComparer.CurrentCultureIgnoreCase;
        return files
            .Select(f => (File: f, Track: ToTrack(f, paths)))
            .OrderBy(x => (x.File.Tags?.AlbumArtist ?? x.File.Tags?.Artist) is null)
            .ThenBy(x => x.File.Tags?.AlbumArtist ?? x.File.Tags?.Artist, text)
            .ThenBy(x => x.File.Tags?.Album is null)
            .ThenBy(x => x.File.Tags?.Album, text)
            .ThenBy(x => x.File.Tags?.DiscNumber ?? int.MaxValue)
            .ThenBy(x => x.File.Tags?.TrackNumber ?? int.MaxValue)
            .ThenBy(x => x.Track.Title, text)
            .ThenBy(x => x.File.Path, paths.Comparer)
            .Select(x => x.File)
            .ToList();
    }
}

/// <summary>A local track can't play: its id isn't in the index, or its file is
/// gone or the index knows it isn't audio. Not retried by streaming.</summary>
public sealed class LocalFileMissingException(string id)
    : InvalidOperationException("The file isn't in the watched folders now, or can't be read as music.")
{
    public string TrackId { get; } = id;
}
