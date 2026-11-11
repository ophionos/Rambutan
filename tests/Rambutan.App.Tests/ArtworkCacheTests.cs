using System.Net;
using Avalonia.Media.Imaging;
using Rambutan.App.Services;

namespace Rambutan.App.Tests;

public class ArtworkCacheTests : IDisposable
{
    // A 1x1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-art-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class PngHandler(Action? onSend = null) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            onSend?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    // Starts the load on the headless UI thread, as RemoteImage does, and returns the
    // caller's thread id with the task. The UI thread is not blocked: the load has to
    // finish without it.
    private static (int Caller, Task<Bitmap?> Load) StartOnUiThread(ArtworkCache cache, string url, int width)
    {
        var caller = 0;
        Task<Bitmap?> load = null!;
        Headless.Run(() =>
        {
            caller = Environment.CurrentManagedThreadId;
            load = cache.GetAsync(url, width);
        });
        return (caller, load);
    }

    [Fact]
    public async Task ACoverOnDisk_IsReadAndDecodedOffTheCallingThread()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "cover.png");
        await File.WriteAllBytesAsync(file, Png);
        using var cache = new ArtworkCache(_dir);
        var decodedOn = -1;
        cache.BeforeDecode = () => decodedOn = Environment.CurrentManagedThreadId;

        var (caller, load) = StartOnUiThread(cache, new Uri(file).AbsoluteUri, 32);

        Assert.NotNull(await load.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.NotEqual(-1, decodedOn);
        Assert.NotEqual(caller, decodedOn);
    }

    [Fact]
    public async Task ADownloadedCover_IsFetchedAndDecodedOffTheCallingThread()
    {
        var fetchedOn = -1;
        var decodedOn = -1;
        using var cache = new ArtworkCache(_dir, new PngHandler(() => fetchedOn = Environment.CurrentManagedThreadId));
        cache.BeforeDecode = () => decodedOn = Environment.CurrentManagedThreadId;

        var (caller, load) = StartOnUiThread(cache, "https://example.test/a.jpg", 32);

        Assert.NotNull(await load.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.NotEqual(caller, fetchedOn);
        Assert.NotEqual(caller, decodedOn);

        // Now it is in the disk cache: a second cache over the same folder reads it.
        using var again = new ArtworkCache(_dir, new PngHandler());
        decodedOn = -1;
        again.BeforeDecode = () => decodedOn = Environment.CurrentManagedThreadId;
        var (caller2, load2) = StartOnUiThread(again, "https://example.test/a.jpg", 32);

        Assert.NotNull(await load2.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.NotEqual(-1, decodedOn);
        Assert.NotEqual(caller2, decodedOn);
    }

    [Fact]
    public async Task ConcurrentLoads_OfOneAndOfManyKeys_AreCorrectAndTheBooksBalance()
    {
        Headless.Start();
        var handler = new PngHandler();
        using var cache = new ArtworkCache(_dir, handler);

        var tasks = Enumerable.Range(0, 200)
            .Select(i => (Key: (i % 10, i % 2),
                Load: Task.Run(() => cache.GetAsync($"https://example.test/{i % 10}.jpg", i % 2 == 0 ? 32 : 0))))
            .ToArray();
        var results = await Task.WhenAll(tasks.Select(t => t.Load)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(results, Assert.NotNull);
        // The same key is one load: every caller of a key gets one instance.
        Assert.All(tasks.GroupBy(t => t.Key),
            g => Assert.Single(g.Select(t => t.Load.Result).Distinct()));
        Assert.True(cache.BooksBalance());
    }

    [Fact]
    public async Task AFailedLoad_GivesNullAndIsNotCached()
    {
        Headless.Start();
        using var cache = new ArtworkCache(_dir, new FailingHandler());

        Assert.Null(await cache.GetAsync("https://example.test/x.jpg", 32).WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Null(await cache.GetAsync("https://example.test/x.jpg", 32).WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(cache.BooksBalance());
    }
}
