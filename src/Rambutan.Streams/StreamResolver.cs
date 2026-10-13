using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;
using Rambutan.Core.Settings;

namespace Rambutan.Streams;

public sealed record ResolvedStream
{
    public required string VideoId { get; init; }
    public required string Url { get; init; }
    public required string Itag { get; init; }
    public required string AudioCodec { get; init; }
    public double? AverageBitrateKbps { get; init; }
    public double? LoudnessDb { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }

    public AudioQuality Quality { get; init; }

    public bool IsUsable(TimeSpan margin) =>
        DateTimeOffset.UtcNow + margin < ExpiresAtUtc;
}

/// <summary>
/// Resolves videoId → googlevideo audio URL via yt-dlp under the user's quality
/// preference, Opus preferred with AAC fallback, with expiry-aware
/// caching of resolved URLs.
/// </summary>
public sealed partial class StreamResolver
{
    /// <summary>yt-dlp's format selector for a quality tier: Opus first, then AAC
    /// (mp4a), then whatever audio is offered.</summary>
    internal static string FormatSelector(AudioQuality quality) => quality switch
    {
        AudioQuality.DataSaver => "worstaudio[acodec^=opus]/worstaudio[acodec^=mp4a]/worstaudio",
        _ => "bestaudio[acodec^=opus]/bestaudio[acodec^=mp4a]/bestaudio",
    };

    // Googlevideo URLs live ~6h; refresh well before the edge stops serving ranges.
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(10);

    private readonly YtDlpManager? _ytDlp;
    private readonly Func<string, AudioQuality, CancellationToken, Task<ResolvedStream>> _resolve;
    private readonly Func<AudioQuality> _quality;
    private readonly ConcurrentDictionary<string, ResolvedStream> _cache = new();
    private readonly Dictionary<string, SharedRun> _pending = [];
    private readonly object _sync = new();

    /// <param name="quality">The live preference, read at each resolution.</param>
    public StreamResolver(YtDlpManager ytDlp, Func<AudioQuality>? quality = null)
    {
        _ytDlp = ytDlp;
        _resolve = ResolveCoreAsync;
        _quality = quality ?? (() => AudioQuality.High);
    }

    /// <summary>For tests: resolution without yt-dlp.</summary>
    internal StreamResolver(
        Func<string, AudioQuality, CancellationToken, Task<ResolvedStream>> resolve,
        Func<AudioQuality>? quality = null)
    {
        _resolve = resolve;
        _quality = quality ?? (() => AudioQuality.High);
    }

    /// <summary>
    /// One yt-dlp run shared by everyone asking for the same video. It is cancelled
    /// only when every caller waiting on it has given up: a hover warm-up abandoned
    /// after the user clicked play must not kill the run the play is waiting on.
    /// </summary>
    private sealed class SharedRun(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public Task<ResolvedStream> Task { get; set; } = null!;
        public int Waiters { get; set; }
    }

    public AudioQuality Quality => _quality();

    /// <summary>True when a usable URL for this video, under the current quality
    /// preference, is cached (warming skips it).</summary>
    public bool IsResolved(string videoId) => TryGetCached(videoId, _quality()) is not null;

    private ResolvedStream? TryGetCached(string videoId, AudioQuality quality) =>
        _cache.TryGetValue(videoId, out var cached) && cached.Quality == quality &&
        cached.IsUsable(ExpiryMargin)
            ? cached
            : null;

    public async Task<ResolvedStream> ResolveAsync(
        string videoId, bool bypassCache = false, CancellationToken ct = default)
    {
        var quality = _quality();
        if (bypassCache)
            return await ResolveAndCacheAsync(videoId, quality, ct);

        if (TryGetCached(videoId, quality) is { } cached)
            return cached;

        // Coalesce concurrent resolves of the same video into one yt-dlp run.
        SharedRun run;
        lock (_sync)
        {
            if (!_pending.TryGetValue(videoId, out run!) || run.Cts.IsCancellationRequested)
            {
                run = new SharedRun(new CancellationTokenSource());
                run.Task = ResolveAndCacheAsync(videoId, quality, run.Cts.Token);
                _pending[videoId] = run;
                var started = run;
                _ = run.Task.ContinueWith(t =>
                {
                    // Observed here: a run every caller abandoned may still fault.
                    _ = t.Exception;
                    lock (_sync)
                    {
                        if (_pending.TryGetValue(videoId, out var current) && current == started)
                            _pending.Remove(videoId);
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            run.Waiters++;
        }

        try
        {
            return await run.Task.WaitAsync(ct);
        }
        finally
        {
            lock (_sync)
            {
                if (--run.Waiters == 0 && !run.Task.IsCompleted)
                    run.Cts.Cancel();
            }
        }
    }

    private async Task<ResolvedStream> ResolveAndCacheAsync(
        string videoId, AudioQuality quality, CancellationToken ct)
    {
        var resolved = await _resolve(videoId, quality, ct);
        _cache[videoId] = resolved;
        EvictExpired();
        return resolved;
    }

    private async Task<ResolvedStream> ResolveCoreAsync(
        string videoId, AudioQuality quality, CancellationToken ct)
    {
        var binary = await _ytDlp!.EnsureAsync(ct);

        // yt-dlp's info dict has no loudness field, but the raw player response
        // it fetches anyway does — --write-pages dumps it, and parsing that keeps
        // loudness inside the yt-dlp boundary (raw InnerTube player calls are
        // login/signature-gated; yt-dlp absorbs those countermeasures).
        // The dumps land in the process working directory: in simulate mode
        // --paths is unreliable (a drive-letter colon breaks its key:value
        // parse), so the run gets a private cwd instead.
        var pagesDir = Path.Combine(
            Path.GetTempPath(), "rambutan-pages", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pagesDir);

        try
        {
            var result = await ProcessRunner.RunAsync(binary,
            [
                "--no-playlist", "--no-warnings", "--quiet",
                "-f", FormatSelector(quality),
                "--print", "%(url)s\t%(format_id)s\t%(acodec)s\t%(abr)s",
                "--write-pages",
                $"https://music.youtube.com/watch?v={videoId}",
            ], ct, workingDirectory: pagesDir);

            if (result.ExitCode != 0)
                throw new StreamResolutionException(videoId, result.StdErr.Trim());

            var fields = result.StdOut.Trim().Split('\t');
            if (fields.Length < 3 || !Uri.IsWellFormedUriString(fields[0], UriKind.Absolute))
                throw new StreamResolutionException(videoId, $"unexpected yt-dlp output: {result.StdOut}");

            var resolved = new ResolvedStream
            {
                VideoId = videoId,
                Url = fields[0],
                Itag = fields[1],
                AudioCodec = fields[2],
                AverageBitrateKbps =
                    fields.Length > 3 &&
                    double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var abr)
                        ? abr
                        : null,
                LoudnessDb = TryReadLoudness(pagesDir),
                ExpiresAtUtc = ParseExpiry(fields[0]),
                Quality = quality,
            };
            return resolved;
        }
        finally
        {
            try
            {
                Directory.Delete(pagesDir, recursive: true);
            }
            catch
            {
                // Leftover dumps in the OS temp dir are harmless.
            }
        }
    }

    /// <summary>
    /// Harvests loudnessDb from the player-response page dump. Best-effort by
    /// design: --write-pages is a debug feature, so any change in its naming or
    /// shape degrades to "no normalization", never to a failed resolve.
    /// </summary>
    private static double? TryReadLoudness(string pagesDir)
    {
        const long maxDumpBytes = 8 << 20;
        try
        {
            foreach (var file in Directory.EnumerateFiles(pagesDir, "*youtubei_v1_player*"))
            {
                if (new FileInfo(file).Length > maxDumpBytes)
                    continue;
                if (ParseLoudnessDb(File.ReadAllText(file)) is { } loudness)
                    return loudness;
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>Extracts playerConfig.audioConfig.loudnessDb from a raw player response.</summary>
    internal static double? ParseLoudnessDb(string playerResponse) =>
        LoudnessRegex().Match(playerResponse) is { Success: true } match &&
        double.TryParse(
            match.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var db)
            ? db
            : null;

    [GeneratedRegex("\"loudnessDb\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)")]
    private static partial Regex LoudnessRegex();

    /// <summary>Long sessions accumulate one entry per played video; drop the dead ones.</summary>
    private void EvictExpired()
    {
        foreach (var entry in _cache)
            if (!entry.Value.IsUsable(TimeSpan.Zero))
                _cache.TryRemove(entry);
    }

    /// <summary>Googlevideo URLs carry their lifetime in the `expire` query parameter.</summary>
    private static DateTimeOffset ParseExpiry(string url)
    {
        var expire = HttpUtility.ParseQueryString(new Uri(url).Query)["expire"];
        return long.TryParse(expire, out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            // Unknown scheme: assume a conservative validity window.
            : DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30);
    }
}

public sealed class StreamResolutionException(string videoId, string detail)
    : Exception($"Failed to resolve stream for '{videoId}': {detail}");
