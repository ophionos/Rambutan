using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.LocalApi;

namespace Rambutan.App.Services;

/// <summary>
/// Covers as the phone remote and the OBS overlay serve them: a downloaded
/// track's own copy first, otherwise through the artwork cache. A row's small
/// cover is the exception: the kept copy is large, so a small one is fetched
/// first, and the kept copy is the fallback (offline). The callers decide
/// which tracks they may serve; this only fetches.
/// </summary>
/// <param name="fetch">A cover's bytes by URL (the artwork cache).</param>
/// <param name="local">A track's cover kept on this computer, by id (<see cref="KeptCovers"/>).</param>
public sealed class CoverSource(
    Func<string, CancellationToken, Task<byte[]>> fetch, Func<string, string?> local)
{
    /// <summary>
    /// Whether the track has a cover to serve. Cheap: the remote asks it under the
    /// coordinator's lock, so only a local file's cover is looked up (in memory).
    /// </summary>
    public bool Has(TrackInfo track) =>
        track.ThumbnailUrl is not null || (LocalIds.IsLocal(track.VideoId) && local(track.VideoId) is not null);

    /// <summary>The track's cover, or null when it has none or it can't be read.</summary>
    public async Task<RemoteArtwork?> GetAsync(TrackInfo? track, ArtworkSize size, string area, CancellationToken ct)
    {
        if (track is null)
            return null;
        try
        {
            var bytes = size == ArtworkSize.Row && track.ThumbnailUrl is { } small
                ? await FetchOrNullAsync(ArtworkUrls.ForRow(small), area, ct).ConfigureAwait(false)
                : null;
            bytes ??= local(track.VideoId) is { } path && File.Exists(path)
                ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)
                : size == ArtworkSize.Full && track.ThumbnailUrl is { } url
                    ? await fetch(url, ct).ConfigureAwait(false)
                    : null;
            return bytes is null ? null : new RemoteArtwork(bytes, ContentTypeOf(bytes));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write(area, $"cover unavailable: {ex.Message}");
            return null;
        }
    }

    private async Task<byte[]?> FetchOrNullAsync(string url, string area, CancellationToken ct)
    {
        try
        {
            return await fetch(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            AppLog.Write(area, $"small cover unavailable, using the kept one: {ex.Message}");
            return null;
        }
    }

    /// <summary>The image type from its first bytes; covers are JPEG, PNG or WebP.</summary>
    public static string ContentTypeOf(byte[] bytes) => bytes switch
    {
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "image/png",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
        _ => "image/jpeg",
    };
}
