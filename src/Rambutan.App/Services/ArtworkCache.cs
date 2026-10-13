using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Rambutan.App.Services;

/// <summary>
/// Artwork loader: byte-budgeted in-memory cache over a content-addressed disk
/// cache. Decodes off the UI thread at the size the caller actually renders —
/// YTM serves 544px art for rows drawn at 40px, and holding those at full size
/// costs ~1 MB each, which is most of the 250 MB memory budget.
/// </summary>
/// <param name="handler">Replaces the network (demo mode); null = real downloads.</param>
public sealed class ArtworkCache(string dataDirectory, HttpMessageHandler? handler = null) : IDisposable
{
    /// <summary>Decoded-pixel budget. Bitmaps still on screen survive eviction —
    /// dropping the cache reference only makes them collectable once unused.</summary>
    private const long MemoryBudgetBytes = 48L * 1024 * 1024;

    /// <summary>On-disk budget (~50 KB per image → thousands of covers). Enforced
    /// at startup and every <see cref="TrimEveryNWrites"/> downloads, evicting
    /// least-recently-read first (reads touch the file timestamp).</summary>
    private const long DiskBudgetBytes = 200L * 1024 * 1024;
    private const int TrimEveryNWrites = 100;

    private readonly string _dir = Path.Combine(dataDirectory, "cache", "artwork");
    private readonly HttpClient _http = handler is null ? new() : new(handler);
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _memory = new();
    private int _writesSinceTrim;

    // Insertion/access order for eviction; guards _bytes too.
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, long> _sizes = [];
    private readonly Lock _sync = new();
    private long _bytes;

    /// <summary>Loads <paramref name="url"/> decoded to <paramref name="decodeWidth"/>
    /// pixels wide (0 = source size). Callers pass roughly 2× their layout size so
    /// the art stays sharp on fractional-DPI displays.</summary>
    public Task<Bitmap?> GetAsync(string url, int decodeWidth = 0)
    {
        var key = $"{decodeWidth}|{url}";
        Touch(key);
        return _memory.GetOrAdd(key, _ => LoadAsync(key, url, decodeWidth));
    }

    /// <summary>The image's bytes, from the disk cache or fetched into it: what a
    /// download keeps as its own cover.</summary>
    public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default) =>
        await File.ReadAllBytesAsync(await EnsureOnDiskAsync(url, ct), ct);

    private async Task<string> EnsureOnDiskAsync(string url, CancellationToken ct)
    {
        var path = Path.Combine(_dir, Convert.ToHexStringLower(
            SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".img");

        if (!File.Exists(path))
        {
            var bytes = await _http.GetByteArrayAsync(url, ct);
            Directory.CreateDirectory(_dir);
            await File.WriteAllBytesAsync(path, bytes, ct);
            if (Interlocked.Increment(ref _writesSinceTrim) >= TrimEveryNWrites)
            {
                Interlocked.Exchange(ref _writesSinceTrim, 0);
                _ = Task.Run(TrimDiskCache);
            }
        }
        else
        {
            // Approximate LRU for disk eviction; best-effort only.
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
        }
        return path;
    }

    private async Task<Bitmap?> LoadAsync(string key, string url, int decodeWidth)
    {
        try
        {
            // A file: URI is a download's own cover; it is read in place, not cached.
            var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : await EnsureOnDiskAsync(url, CancellationToken.None);

            await using var stream = File.OpenRead(path);
            var bitmap = decodeWidth > 0
                ? Bitmap.DecodeToWidth(stream, decodeWidth)
                : new Bitmap(stream);

            Admit(key, bitmap);
            return bitmap;
        }
        catch
        {
            _memory.TryRemove(key, out _); // a failed fetch must not cache forever
            return null; // missing art renders as an empty placeholder
        }
    }

    private void Touch(string key)
    {
        lock (_sync)
        {
            if (_order.Remove(key))
                _order.AddLast(key);
        }
    }

    /// <summary>Charges the decoded bitmap against the budget, evicting the
    /// least-recently-used entries until it fits.</summary>
    private void Admit(string key, Bitmap bitmap)
    {
        var size = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;

        lock (_sync)
        {
            _order.AddLast(key);
            _sizes[key] = size;
            _bytes += size;

            while (_bytes > MemoryBudgetBytes && _order.First is { } oldest)
            {
                if (oldest.Value == key)
                    break; // never evict what we just admitted

                _order.RemoveFirst();
                if (_sizes.Remove(oldest.Value, out var evicted))
                    _bytes -= evicted;
                _memory.TryRemove(oldest.Value, out _);
            }
        }
    }

    /// <summary>Evicts least-recently-read artwork files until the directory fits
    /// the disk budget (with slack so it doesn't re-trim every write). Safe to run
    /// concurrently with loads: a deleted file just re-downloads on next miss.</summary>
    public void TrimDiskCache()
    {
        try
        {
            if (!Directory.Exists(_dir))
                return;

            var files = new DirectoryInfo(_dir).GetFiles("*.img");
            var total = files.Sum(f => f.Length);
            if (total <= DiskBudgetBytes)
                return;

            var target = DiskBudgetBytes * 9 / 10;
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                try
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                }
                catch
                {
                    // In use or already gone — skip it.
                }
                if (total <= target)
                    return;
            }
        }
        catch
        {
            // Trimming is housekeeping; never let it surface.
        }
    }

    public void Dispose() => _http.Dispose();
}
