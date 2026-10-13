using Rambutan.Core.Downloads;
using Rambutan.Core.Models;

namespace Rambutan.Streams;

/// <summary>
/// Fetches a download: a copy of the cached file when the audio cache already
/// holds the track at the current quality, else a ranged-chunk download of a
/// freshly resolved googlevideo URL.
/// </summary>
public sealed class GoogleVideoFetcher : IAudioFetcher, IDisposable
{
    private readonly StreamResolver _resolver;
    private readonly AudioDiskCache? _cache;
    private readonly HttpClient _http;
    private readonly RangedFetcher _fetcher;

    public GoogleVideoFetcher(StreamResolver resolver, AudioDiskCache? cache = null)
        : this(resolver, cache, new HttpClient())
    {
    }

    /// <summary>For tests: downloads through <paramref name="http"/>.</summary>
    internal GoogleVideoFetcher(StreamResolver resolver, AudioDiskCache? cache, HttpClient http)
    {
        _resolver = resolver;
        _cache = cache;
        _http = http;
        _fetcher = new RangedFetcher(http);
    }

    public async Task<FetchedAudio> FetchAsync(
        TrackInfo track, Stream target, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var videoId = track.VideoId;
        var quality = _resolver.Quality;
        if (_cache?.TryGet(videoId, quality) is { } cached && await TryCopyAsync(cached.Path, target, progress, ct))
            return new FetchedAudio(cached.AudioCodec, Itag: null, quality, cached.LoudnessDb);

        var stream = await _resolver.ResolveAsync(videoId, bypassCache: false, ct);
        var total = await _fetcher.FetchAsync(
            stream.Url, target, (written, length) => progress.Report(new DownloadProgress(written, length)), ct);
        if (total is null || target.Length != total)
            throw new IOException("the server stopped sending the audio before the end");

        return new FetchedAudio(stream.AudioCodec, stream.Itag, stream.Quality, stream.LoudnessDb);
    }

    /// <summary>False when the cached file went away or couldn't be read; the
    /// caller downloads instead.</summary>
    private static async Task<bool> TryCopyAsync(
        string path, Stream target, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        try
        {
            // Shared read: the file may be playing.
            await using var source = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await source.CopyToAsync(target, ct);
            progress.Report(new DownloadProgress(target.Length, target.Length));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            target.SetLength(0);
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
