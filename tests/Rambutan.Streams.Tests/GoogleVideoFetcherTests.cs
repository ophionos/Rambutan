using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Rambutan.Core.Downloads;
using Rambutan.Core.Models;
using Rambutan.Core.Settings;

namespace Rambutan.Streams.Tests;

/// <summary>The download fetcher: ranged chunks from googlevideo, or a copy
/// from the audio cache; and the cache leaving the downloads folder alone.</summary>
public sealed class GoogleVideoFetcherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));

    private string DataDir => Path.Combine(_root, "data");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static TrackInfo Track(string id) => new()
    {
        VideoId = id,
        Title = $"Song {id}",
        Artists = [new ArtistRef("Artist", "UC1")],
        Duration = TimeSpan.FromSeconds(200),
    };

    private static readonly IProgress<DownloadProgress> NoProgress = new Progress<DownloadProgress>();

    [Fact]
    public async Task The_cache_cap_and_eviction_leave_the_downloads_folder_alone()
    {
        // The audio cache and the downloads folder are siblings under cache/.
        var download = Path.Combine(DataDir, "cache", "downloads", "vid00000001.webm");
        Directory.CreateDirectory(Path.GetDirectoryName(download)!);
        File.WriteAllText(download, "audio");
        var cache = new AudioDiskCache(Path.Combine(DataDir, "cache", "audio")) { MaxSizeBytes = 1 };

        await cache.StoreAsync("vid00000002", "251", "opus", AudioQuality.High,
            new MemoryStream(Encoding.UTF8.GetBytes("much bigger than one byte")));
        cache.EnforceCap();
        cache.Remove("vid00000001");

        Assert.True(File.Exists(download));
    }

    [Fact]
    public async Task The_fetcher_downloads_in_ranged_chunks_with_progress()
    {
        var body = new byte[(1 << 20) + 300];
        Random.Shared.NextBytes(body);
        var resolver = new StreamResolver((id, quality, _) => Task.FromResult(new ResolvedStream
        {
            VideoId = id,
            Url = $"https://cdn.test/{id}",
            Itag = "251",
            AudioCodec = "opus",
            LoudnessDb = -2,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(6),
            Quality = quality,
        }));
        using var fetcher = new GoogleVideoFetcher(resolver, cache: null, new HttpClient(new RangedHandler(body)));
        var reports = new List<DownloadProgress>();
        using var target = new MemoryStream();

        var fetched = await fetcher.FetchAsync(Track("vid00000001"), target, new SyncProgress(reports.Add), default);

        Assert.Equal(body, target.ToArray());
        Assert.Equal(new FetchedAudio("opus", "251", AudioQuality.High, -2), fetched);
        Assert.Equal([(1L << 20, (long?)body.Length), (body.Length, body.Length)],
            reports.Select(r => (r.Bytes, r.Total)));
    }

    [Fact]
    public async Task The_fetcher_copies_a_cached_track_instead_of_downloading_it()
    {
        var cache = new AudioDiskCache(Path.Combine(DataDir, "cache", "audio")) { MaxSizeBytes = 1 << 20 };
        await cache.StoreAsync("vid00000001", "251", "opus", AudioQuality.High,
            new MemoryStream(Encoding.UTF8.GetBytes("cached")));
        var resolver = new StreamResolver((_, _, _) => throw new InvalidOperationException("no network"));
        using var fetcher = new GoogleVideoFetcher(resolver, cache, new HttpClient());
        using var target = new MemoryStream();

        var fetched = await fetcher.FetchAsync(Track("vid00000001"), target, NoProgress, default);

        Assert.Equal("cached", Encoding.UTF8.GetString(target.ToArray()));
        Assert.Equal("opus", fetched.AudioCodec);
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    /// <summary>Serves <paramref name="body"/> to ranged GETs only, like googlevideo.</summary>
    private sealed class RangedHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.Range?.Ranges.Single() is not { From: { } from, To: { } to })
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            var end = Math.Min(to, body.Length - 1);
            var content = new ByteArrayContent(body, (int)from, (int)(end - from + 1));
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, end, body.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }
    }
}
