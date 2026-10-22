using System;
using Rambutan.Core.Local;
using Rambutan.LocalLibrary;
using Rambutan.Streams;

namespace Rambutan.App.Services;

/// <summary>
/// The cover kept on this computer for a track id: a local file's own cover
/// (from the local index's cover cache) or, without one, its match's
/// (<see cref="YouTubeMatches.CoverPathFor"/>); else a download's copy. Null when
/// there is none; the caller falls back to the track's thumbnail URL.
/// </summary>
public sealed class KeptCovers(DownloadStore downloads, LocalFileIndex local, Func<string, string?>? matched = null)
{
    public string? PathFor(string id) =>
        LocalIds.IsLocal(id) ? local.Find(id)?.CoverPath ?? matched?.Invoke(id) : downloads.ArtworkPathFor(id);
}
