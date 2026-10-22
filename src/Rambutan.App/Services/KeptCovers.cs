using Rambutan.Core.Local;
using Rambutan.LocalLibrary;
using Rambutan.Streams;

namespace Rambutan.App.Services;

/// <summary>
/// The cover kept on this computer for a track id: a local file's embedded cover
/// (from the local index's cover cache), else a download's copy. Null when there
/// is none; the caller falls back to the track's thumbnail URL.
/// </summary>
public sealed class KeptCovers(DownloadStore downloads, LocalFileIndex local)
{
    public string? PathFor(string id) =>
        LocalIds.IsLocal(id) ? local.Find(id)?.CoverPath : downloads.ArtworkPathFor(id);
}
