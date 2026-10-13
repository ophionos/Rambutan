using Rambutan.Core.Settings;

namespace Rambutan.Streams;

public sealed record CachedAudio(string Path, string AudioCodec, double? LoudnessDb = null);

/// <summary>
/// Disk cache for fully-fetched audio, keyed videoId+itag, with a size cap and
/// LRU eviction. Files are written as `.part` and renamed only when
/// complete, so a crash mid-download can never produce a servable partial file.
/// LRU order rides on the file write time, touched on every hit.
/// <para>
/// Each entry records the quality tier it was fetched under, so a lookup
/// can refuse an entry worse than the current preference: under High only a High
/// entry serves, while under Data saver any entry does, because a local file costs
/// no data whatever its bitrate. Entries from before the tier token existed count
/// as unknown, which only Data saver accepts; under High they are fetched again
/// once and replaced.
/// </para>
/// </summary>
public sealed class AudioDiskCache(string directory)
{
    private const string Extension = ".audio";
    private const string PartExtension = ".part";
    private static readonly TimeSpan StalePartAge = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Size cap in bytes. Zero or negative disables the cache.</summary>
    public long MaxSizeBytes { get; set; }

    public bool Enabled => MaxSizeBytes > 0;

    /// <summary>A complete entry for the video that is good enough for
    /// <paramref name="quality"/>, a High one first; touches LRU order.</summary>
    public CachedAudio? TryGet(string videoId, AudioQuality quality)
    {
        if (!Enabled)
            return null;

        try
        {
            if (Best(videoId, quality) is not { } entry)
                return null;

            File.SetLastWriteTimeUtc(entry.Path, DateTime.UtcNow);
            return new CachedAudio(entry.Path, entry.AudioCodec, entry.LoudnessDb);
        }
        catch
        {
            // Cache misses are always safe; the caller resolves normally.
        }

        return null;
    }

    public bool Contains(string videoId, AudioQuality quality)
    {
        try
        {
            return Best(videoId, quality) is not null;
        }
        catch
        {
            return false;
        }
    }

    private Entry? Best(string videoId, AudioQuality quality) =>
        Entries(videoId)
            .Where(e => Satisfies(e.Tier, quality))
            .OrderBy(e => e.Tier is AudioQuality.High ? 0 : 1)
            .FirstOrDefault();

    /// <summary>Under High only a High entry will do; under Data saver anything
    /// local will, a High file or one of unknown tier included.</summary>
    private static bool Satisfies(AudioQuality? tier, AudioQuality wanted) =>
        wanted is AudioQuality.DataSaver || tier == wanted;

    private sealed record Entry(string Path, AudioQuality? Tier, string AudioCodec, double? LoudnessDb);

    private IEnumerable<Entry> Entries(string videoId)
    {
        if (!Directory.Exists(directory))
            yield break;

        foreach (var path in Directory.EnumerateFiles(
            directory, Sanitize(videoId) + "~*" + Extension))
        {
            if (Parse(path) is { } entry)
                yield return entry;
        }
    }

    /// <summary>
    /// Reads <c>id~itag~codec~tier[~loudness]</c>. Older names have no tier,
    /// <c>id~itag~codec[~loudness]</c>; a tier token is never a number, so the
    /// fourth token tells the two apart.
    /// </summary>
    private static Entry? Parse(string path)
    {
        var parts = Path.GetFileName(path)[..^Extension.Length].Split('~');
        if (parts.Length < 3)
            return null;

        AudioQuality? tier = parts.Length > 3 ? ParseTier(parts[3]) : null;
        var loudnessIndex = tier is null ? 3 : 4;
        // The loudness token is optional: entries written before it existed stay
        // servable, just without normalization.
        double? loudness =
            parts.Length > loudnessIndex && double.TryParse(
                parts[loudnessIndex], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var db)
                ? db
                : null;
        return new Entry(path, tier, parts[2], loudness);
    }

    private const string HighToken = "hq";
    private const string DataSaverToken = "ds";

    private static AudioQuality? ParseTier(string token) => token switch
    {
        HighToken => AudioQuality.High,
        DataSaverToken => AudioQuality.DataSaver,
        _ => null,
    };

    private static string TierToken(AudioQuality quality) =>
        quality is AudioQuality.DataSaver ? DataSaverToken : HighToken;

    /// <summary>Drops all entries for the video — the coordinator's bypassCache
    /// retry lands here, so a corrupt cached file self-heals on the next play.</summary>
    public void Remove(string videoId)
    {
        if (!Directory.Exists(directory))
            return;

        var prefix = Sanitize(videoId) + "~*";
        foreach (var pattern in new[] { prefix + Extension, prefix + Extension + PartExtension })
            foreach (var path in Directory.EnumerateFiles(directory, pattern))
                TryDelete(path);
    }

    /// <summary>
    /// Writes a complete entry from <paramref name="content"/>. If
    /// <paramref name="expectedLength"/> is given and the byte count differs, the
    /// download was truncated and nothing is kept.
    /// </summary>
    public Task StoreAsync(
        string videoId, string itag, string audioCodec, AudioQuality quality, Stream content,
        long? expectedLength = null, CancellationToken ct = default) =>
        StoreAsync(videoId, itag, audioCodec, quality, async (file, token) =>
        {
            await content.CopyToAsync(file, token);
            return expectedLength ?? file.Length;
        }, loudnessDb: null, ct);

    /// <summary>
    /// Writes a complete entry streamed by <paramref name="writeAsync"/> straight
    /// into the entry's <c>.part</c> file — nothing is buffered here. The callback
    /// returns the expected byte count, or null to discard; a count that differs
    /// from what was written means truncation and nothing is kept. The write runs
    /// outside the gate so concurrent fills of different videos don't serialize;
    /// per-video dedup is the caller's job. Once committed, the entry replaces the
    /// video's other entries, so a High fill drops the Data saver file it outranks.
    /// </summary>
    public async Task StoreAsync(
        string videoId, string itag, string audioCodec, AudioQuality quality,
        Func<Stream, CancellationToken, Task<long?>> writeAsync,
        double? loudnessDb = null, CancellationToken ct = default)
    {
        var finalPath = FinalPath(videoId, itag, audioCodec, quality, loudnessDb);
        if (!Enabled || File.Exists(finalPath))
            return;

        var partPath = finalPath + PartExtension;

        try
        {
            Directory.CreateDirectory(directory);
            long written;
            long? expected;
            await using (var file = File.Create(partPath))
            {
                expected = await writeAsync(file, ct);
                written = file.Length;
            }

            if (expected is null || written != expected)
            {
                TryDelete(partPath);
                return;
            }

            await _writeGate.WaitAsync(ct);
            try
            {
                if (File.Exists(finalPath))
                    TryDelete(partPath);
                else
                {
                    File.Move(partPath, finalPath, overwrite: true);
                    // A file in playback stays locked; Best() still picks by tier,
                    // and a later eviction collects it.
                    foreach (var other in Entries(videoId).Where(e => e.Path != finalPath).ToList())
                        TryDelete(other.Path);
                    EvictOverCap();
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    private string FinalPath(
        string videoId, string itag, string audioCodec, AudioQuality quality, double? loudnessDb)
    {
        // Loudness rides the filename like the other metadata; "R" round-trips.
        var loudness = loudnessDb is { } db
            ? "~" + db.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            : "";
        return Path.Combine(
            directory,
            $"{Sanitize(videoId)}~{Sanitize(itag)}~{Sanitize(audioCodec)}~{TierToken(quality)}{loudness}{Extension}");
    }

    /// <summary>Applies the current cap immediately (settings change). A cap of
    /// zero clears the cache.</summary>
    public void EnforceCap()
    {
        _writeGate.Wait();
        try
        {
            EvictOverCap();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void EvictOverCap()
    {
        if (!Directory.Exists(directory))
            return;

        var dir = new DirectoryInfo(directory);
        var entries = dir.GetFiles("*" + Extension);
        var total = entries.Sum(f => f.Length);

        foreach (var entry in entries.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= Math.Max(MaxSizeBytes, 0))
                break;
            var length = entry.Length; // Delete() drops the cached attributes
            try
            {
                entry.Delete();
                total -= length;
            }
            catch
            {
                // Locked file (in playback) — skip; the next eviction retries.
            }
        }

        // Leftover .part files from a crashed download.
        foreach (var part in dir.GetFiles("*" + PartExtension))
            if (DateTime.UtcNow - part.LastWriteTimeUtc > StalePartAge)
                TryDelete(part.FullName);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    /// <summary>Filename-safe form; '~' is reserved as the key separator.</summary>
    private static string Sanitize(string value)
    {
        if (value.AsSpan().IndexOfAny(InvalidChars) < 0)
            return value;

        return string.Create(value.Length, value, (chars, source) =>
        {
            for (var i = 0; i < chars.Length; i++)
                chars[i] = InvalidChars.Contains(source[i]) ? '_' : source[i];
        });
    }

    private static readonly System.Buffers.SearchValues<char> InvalidChars =
        System.Buffers.SearchValues.Create([.. Path.GetInvalidFileNameChars(), '~']);
}
