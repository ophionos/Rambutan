using Rambutan.Core.Local;
using Rambutan.Core.Playback;

namespace Rambutan.LocalLibrary;

/// <summary>
/// <see cref="IStreamSource"/> decorator that answers a <c>local:</c> id with its
/// file from the index; every other id goes to <c>inner</c>. A local id never
/// reaches <c>inner</c>, not even on the fault retry (bypassCache): there is no
/// stream behind a file, and the id must not go to YouTube.
/// </summary>
/// <param name="decoderReady">Completes when libav can decode; the file is handed
/// over only then. It doesn't wait for yt-dlp, so local files play without it.</param>
public sealed class LocalFirstSource(
    IStreamSource inner, LocalFileIndex index, Func<CancellationToken, Task> decoderReady)
    : IStreamSource, IStreamWarmer, IOfflineAvailability
{
    /// <exception cref="LocalFileMissingException">A local id whose file isn't
    /// there now, isn't audio, or isn't in the index.</exception>
    public async Task<ResolvedMedia> ResolveAsync(
        string videoId, bool bypassCache = false, CancellationToken ct = default)
    {
        if (!LocalIds.IsLocal(videoId))
            return await inner.ResolveAsync(videoId, bypassCache, ct);

        var file = Playable(videoId) ?? throw new LocalFileMissingException(videoId);
        await decoderReady(ct);
        return new ResolvedMedia(file.Path, file.Tags!.Codec, LocalTracks.LoudnessDb(file.Tags));
    }

    public bool IsWarm(string videoId) =>
        LocalIds.IsLocal(videoId) || inner is not IStreamWarmer warmer || warmer.IsWarm(videoId);

    public Task WarmAsync(string videoId, CancellationToken ct) =>
        !LocalIds.IsLocal(videoId) && inner is IStreamWarmer warmer
            ? warmer.WarmAsync(videoId, ct)
            : Task.CompletedTask;

    public bool IsAvailableOffline(string videoId) =>
        LocalIds.IsLocal(videoId)
            ? Playable(videoId) is not null
            : inner is IOfflineAvailability offline && offline.IsAvailableOffline(videoId);

    private LocalFile? Playable(string id) =>
        index.Find(id) is { IsMissing: false, IsUnreadable: false } file && File.Exists(file.Path) ? file : null;
}
