using System.Text.Json;
using Rambutan.Core.Diagnostics;
using Rambutan.Lyrics.Providers;

namespace Rambutan.Lyrics.Store;

/// <summary>
/// Per-track lookup results, one versioned JSON file per video id under
/// <c>{dataDir}/lyrics/cache/</c>. Misses are cached too, for
/// <see cref="NegativeTtl"/>, so replaying an uncovered track doesn't ask the
/// providers again on every play; lyrics and instrumental answers are kept until
/// removed. <see cref="LyricsStatus.Unavailable"/> is never cached, because the
/// next attempt may succeed. Never throws: a cache that can't be read or written
/// is a cache miss, logged.
/// </summary>
/// <remarks>
/// Version 2 came with LRCLIB's word timings. Lyrics found under version 1 are
/// still served, but marked stale, so <see cref="LyricsService"/> looks them up
/// again when it may and keeps them when it can't.
/// </remarks>
public sealed class LyricsCache(string directory, TimeProvider? clock = null)
{
    public const int CurrentVersion = 2;

    public static readonly TimeSpan DefaultNegativeTtl = TimeSpan.FromDays(3);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string DirectoryPath { get; } = directory;

    /// <summary>How long a "no lyrics found" answer is trusted.</summary>
    public TimeSpan NegativeTtl { get; init; } = DefaultNegativeTtl;

    /// <summary>The cached lookup, or null on a miss, an expired miss, or an unreadable file.</summary>
    public LyricsLookup? TryGet(string videoId) => TryGet(videoId, out _);

    /// <summary>As <see cref="TryGet(string)"/>; <paramref name="stale"/> says the
    /// lyrics were found by an older version and are worth looking up again.</summary>
    public LyricsLookup? TryGet(string videoId, out bool stale)
    {
        stale = false;
        if (PathFor(videoId) is not { } path)
            return null;

        try
        {
            if (!File.Exists(path))
                return null;

            var file = JsonSerializer.Deserialize(File.ReadAllText(path), LyricsJsonContext.Default.CachedLyricsFile);
            // Version 1 has the same shape; a newer version than this build's isn't read.
            if (file is null || file.Version is < 1 or > CurrentVersion)
                return null;

            switch (file.Status)
            {
                case "notFound":
                    return _clock.GetUtcNow() - file.FetchedAt < NegativeTtl ? LyricsLookup.NotFound : null;
                case "instrumental" when file.Document?.ToModel() is { } empty:
                    return new LyricsLookup(LyricsStatus.Instrumental, empty);
                case "found" when file.Document?.ToModel() is { } doc:
                    stale = file.Version < CurrentVersion;
                    return new LyricsLookup(LyricsStatus.Found, doc);
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("lyrics", $"Ignoring unreadable cache entry for {videoId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Stores a lookup result. <see cref="LyricsStatus.Unavailable"/> and
    /// <see cref="LyricsStatus.NotLookedUp"/> are ignored.</summary>
    public void Put(string videoId, LyricsLookup lookup)
    {
        var status = lookup.Status switch
        {
            LyricsStatus.Found => "found",
            LyricsStatus.Instrumental => "instrumental",
            LyricsStatus.NotFound => "notFound",
            _ => null,
        };
        if (status is null || PathFor(videoId) is not { } path)
            return;

        try
        {
            var file = new CachedLyricsFile
            {
                Version = CurrentVersion,
                FetchedAt = _clock.GetUtcNow(),
                Status = status,
                Document = lookup.Document is { } doc ? CachedDocument.From(doc) : null,
            };
            LyricsFiles.WriteAtomic(path, JsonSerializer.Serialize(file, LyricsJsonContext.Default.CachedLyricsFile));
        }
        catch (Exception ex)
        {
            AppLog.Write("lyrics", $"Couldn't cache lyrics for {videoId}: {ex.Message}");
        }
    }

    public void Remove(string videoId)
    {
        if (PathFor(videoId) is not { } path)
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Write("lyrics", $"Couldn't remove cached lyrics for {videoId}: {ex.Message}");
        }
    }

    private string? PathFor(string videoId) =>
        LyricsFiles.FileName(videoId) is { } name ? Path.Combine(DirectoryPath, name) : null;
}
