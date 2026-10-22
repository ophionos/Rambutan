namespace Rambutan.Core.Local;

/// <summary>
/// Reads what a local audio file says about itself: its tags, length, loudness
/// and cover. The format comes from the file's content, never its extension: a
/// misnamed file read as the wrong format gives wrong data without an error.
/// </summary>
public interface IMediaFileReader
{
    /// <summary>The file's details; null when it isn't audio the reader can open
    /// (not audio at all, damaged, or gone). Called from several scan threads at
    /// once. Throws only for <see cref="OperationCanceledException"/>.</summary>
    LocalFileTags? Read(string path);
}

/// <summary>
/// One file's details as its tags give them. Every text tag is null when the
/// file doesn't have it; nothing is guessed from the file name here.
/// </summary>
/// <param name="Codec">The audio codec's short name ("mp3", "flac", "opus").</param>
/// <param name="TrackGainDb">The ReplayGain track gain: the change in dB that
/// brings the track to the ReplayGain reference level.</param>
/// <param name="AlbumGainDb">The same for the album.</param>
public sealed record LocalFileTags(
    string Codec,
    TimeSpan? Duration = null,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? AlbumArtist = null,
    string? Genre = null,
    int? TrackNumber = null,
    int? DiscNumber = null,
    int? Year = null,
    double? TrackGainDb = null,
    double? AlbumGainDb = null,
    LocalCover? Cover = null);

/// <summary>An embedded cover picture.</summary>
/// <param name="Extension">The file extension its format takes, with the dot
/// (".jpg", ".png").</param>
public sealed record LocalCover(byte[] Data, string Extension);
