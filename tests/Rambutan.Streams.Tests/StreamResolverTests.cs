using Rambutan.Core.Settings;

namespace Rambutan.Streams.Tests;

public sealed class StreamResolverTests
{
    [Theory]
    [InlineData("""{"playerConfig":{"audioConfig":{"loudnessDb":4.3999996,"perceptualLoudnessDb":-9.6}}}""", 4.3999996)]
    [InlineData("""{"audioConfig":{"loudnessDb":-3.25}}""", -3.25)]
    [InlineData("""{"audioConfig":{ "loudnessDb" : 0 }}""", 0)]
    public void ParseLoudnessDb_extracts_the_value(string playerResponse, double expected)
    {
        Assert.Equal(expected, StreamResolver.ParseLoudnessDb(playerResponse));
    }

    [Theory]
    [InlineData("""{"audioConfig":{"perceptualLoudnessDb":-9.6}}""")]
    [InlineData("""{"title":"loudnessDb the song"}""")]
    [InlineData("")]
    public void ParseLoudnessDb_returns_null_when_absent(string playerResponse)
    {
        Assert.Null(StreamResolver.ParseLoudnessDb(playerResponse));
    }

    /// <summary>Stands in for yt-dlp: each run waits until the test finishes it.</summary>
    private sealed class ScriptedYtDlp
    {
        public List<(string VideoId, AudioQuality Quality)> Runs { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public bool Immediate { get; init; }

        public Task<ResolvedStream> Resolve(string videoId, AudioQuality quality, CancellationToken ct)
        {
            lock (Runs)
            {
                Runs.Add((videoId, quality));
                Tokens.Add(ct);
            }
            var stream = new ResolvedStream
            {
                VideoId = videoId,
                Url = $"https://example.invalid/{videoId}/{quality}",
                Itag = quality == AudioQuality.High ? "251" : "249",
                AudioCodec = "opus",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(6),
                Quality = quality,
            };
            if (Immediate)
                return Task.FromResult(stream);

            var tcs = new TaskCompletionSource<ResolvedStream>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));
            _finish = () => tcs.TrySetResult(stream);
            return tcs.Task;
        }

        private Action? _finish;

        public void FinishLast() => _finish!();
    }

    [Fact]
    public void FormatSelector_prefers_opus_then_aac_in_each_tier()
    {
        Assert.Equal("bestaudio[acodec^=opus]/bestaudio[acodec^=mp4a]/bestaudio",
            StreamResolver.FormatSelector(AudioQuality.High));
        Assert.Equal("worstaudio[acodec^=opus]/worstaudio[acodec^=mp4a]/worstaudio",
            StreamResolver.FormatSelector(AudioQuality.DataSaver));
    }

    [Fact]
    public async Task Resolution_uses_the_quality_preferred_at_the_time()
    {
        var quality = AudioQuality.High;
        var ytDlp = new ScriptedYtDlp { Immediate = true };
        var resolver = new StreamResolver(ytDlp.Resolve, () => quality);

        await resolver.ResolveAsync("a");
        quality = AudioQuality.DataSaver;
        await resolver.ResolveAsync("b");

        Assert.Equal([("a", AudioQuality.High), ("b", AudioQuality.DataSaver)], ytDlp.Runs);
    }

    [Fact]
    public async Task A_cached_url_serves_only_its_own_quality()
    {
        var quality = AudioQuality.High;
        var ytDlp = new ScriptedYtDlp { Immediate = true };
        var resolver = new StreamResolver(ytDlp.Resolve, () => quality);

        await resolver.ResolveAsync("a");
        await resolver.ResolveAsync("a");
        Assert.Single(ytDlp.Runs);
        Assert.True(resolver.IsResolved("a"));

        quality = AudioQuality.DataSaver;
        Assert.False(resolver.IsResolved("a"));
        var low = await resolver.ResolveAsync("a");

        Assert.Equal(2, ytDlp.Runs.Count);
        Assert.Equal(AudioQuality.DataSaver, low.Quality);
    }

    [Fact]
    public async Task Concurrent_resolves_share_one_run()
    {
        var ytDlp = new ScriptedYtDlp();
        var resolver = new StreamResolver(ytDlp.Resolve);

        var first = resolver.ResolveAsync("a");
        var second = resolver.ResolveAsync("a");
        ytDlp.FinishLast();

        Assert.Same(await first, await second);
        Assert.Single(ytDlp.Runs);
    }

    [Fact]
    public async Task A_caller_giving_up_leaves_the_run_to_those_still_waiting()
    {
        // The hover warm-up case: the pointer leaves after the click, and the play
        // must keep the run the warm-up started.
        var ytDlp = new ScriptedYtDlp();
        var resolver = new StreamResolver(ytDlp.Resolve);
        using var warmup = new CancellationTokenSource();

        var warming = resolver.ResolveAsync("a", ct: warmup.Token);
        var playing = resolver.ResolveAsync("a");
        warmup.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming);

        Assert.False(ytDlp.Tokens[0].IsCancellationRequested);
        ytDlp.FinishLast();
        Assert.Equal("a", (await playing).VideoId);
        Assert.Single(ytDlp.Runs);
    }

    [Fact]
    public async Task The_run_is_cancelled_once_every_caller_gave_up()
    {
        var ytDlp = new ScriptedYtDlp();
        var resolver = new StreamResolver(ytDlp.Resolve);
        using var warmup = new CancellationTokenSource();

        var warming = resolver.ResolveAsync("a", ct: warmup.Token);
        warmup.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming);

        Assert.True(ytDlp.Tokens[0].IsCancellationRequested);
        Assert.False(resolver.IsResolved("a"));
    }

    [Fact]
    public async Task A_resolve_after_an_abandoned_run_starts_afresh()
    {
        var ytDlp = new ScriptedYtDlp();
        var resolver = new StreamResolver(ytDlp.Resolve);
        using var warmup = new CancellationTokenSource();

        var warming = resolver.ResolveAsync("a", ct: warmup.Token);
        warmup.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming);

        var playing = resolver.ResolveAsync("a");
        ytDlp.FinishLast();

        Assert.Equal("a", (await playing).VideoId);
        Assert.Equal(2, ytDlp.Runs.Count);
    }

    [Fact]
    public async Task A_cached_entry_below_the_preference_plays_offline_but_not_online()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new AudioDiskCache(dir) { MaxSizeBytes = 1 << 20 };
            await cache.StoreAsync("a", "249", "opus", AudioQuality.DataSaver, new MemoryStream([1, 2, 3]));
            var resolver = new StreamResolver(new ScriptedYtDlp().Resolve, () => AudioQuality.High);
            using var source = new CachingStreamSource(resolver, cache);

            // Online it's a miss (the High copy should replace it); offline it plays.
            Assert.False(source.IsWarm("a"));
            Assert.True(source.IsAvailableOffline("a"));
            Assert.Equal("opus", source.TryLocal("a")?.AudioCodec);
            Assert.StartsWith(dir, source.TryLocal("a")?.Url);
            Assert.False(source.IsAvailableOffline("b"));
            Assert.Null(source.TryLocal("b"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_cached_entry_below_the_preference_does_not_count_as_warm()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new AudioDiskCache(dir) { MaxSizeBytes = 1 << 20 };
            await cache.StoreAsync("a", "249", "opus", AudioQuality.DataSaver, new MemoryStream([1, 2, 3]));
            var quality = AudioQuality.High;
            var resolver = new StreamResolver(new ScriptedYtDlp().Resolve, () => quality);
            using var source = new CachingStreamSource(resolver, cache);

            Assert.False(source.IsWarm("a"));
            quality = AudioQuality.DataSaver;
            Assert.True(source.IsWarm("a"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Warming_resolves_without_downloading()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var ytDlp = new ScriptedYtDlp { Immediate = true };
            var resolver = new StreamResolver(ytDlp.Resolve);
            using var source = new CachingStreamSource(
                resolver, new AudioDiskCache(dir) { MaxSizeBytes = 1 << 20 });

            Assert.False(source.IsWarm("a"));
            await source.WarmAsync("a", CancellationToken.None);

            Assert.True(source.IsWarm("a"));
            Assert.False(Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any());

            // The play that follows needs no second run. Asked of the resolver, not
            // the source: a play through the source starts a background cache fill,
            // whose open .part file Windows won't let the cleanup below delete.
            await resolver.ResolveAsync("a");
            Assert.Single(ytDlp.Runs);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Warming_a_url_that_is_cut_off_resolves_again()
    {
        // The play would fail its open and resolve again then; warm-up does
        // it ahead, so the play finds the fresh URL in the resolver's cache.
        var dir = Path.Combine(Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var ytDlp = new ScriptedYtDlp { Immediate = true };
            var resolver = new StreamResolver(ytDlp.Resolve);
            var checks = 0;
            using var source = new CachingStreamSource(resolver, new AudioDiskCache(dir) { MaxSizeBytes = 1 << 20 })
            {
                CutOffCheck = (_, _) => Task.FromResult(++checks == 1),
            };

            await source.WarmAsync("a", CancellationToken.None);

            Assert.Equal(1, checks);
            Assert.Equal(2, ytDlp.Runs.Count);
            Assert.True(source.IsWarm("a"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Warming_a_url_that_is_served_whole_resolves_once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var ytDlp = new ScriptedYtDlp { Immediate = true };
            using var source = new CachingStreamSource(
                new StreamResolver(ytDlp.Resolve), new AudioDiskCache(dir) { MaxSizeBytes = 1 << 20 })
            {
                CutOffCheck = (_, _) => Task.FromResult(false),
            };

            await source.WarmAsync("a", CancellationToken.None);

            Assert.Single(ytDlp.Runs);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
