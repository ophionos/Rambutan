using System.Text;
using Rambutan.Core.Settings;

namespace Rambutan.Streams.Tests;

public sealed class AudioDiskCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));

    private AudioDiskCache NewCache(long maxBytes = 1024 * 1024) =>
        new(_dir) { MaxSizeBytes = maxBytes };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static Task StoreAsync(AudioDiskCache cache, string videoId, string bytes,
        string itag = "251", string codec = "opus", long? expectedLength = null,
        AudioQuality quality = AudioQuality.High) =>
        cache.StoreAsync(videoId, itag, codec, quality,
            new MemoryStream(Encoding.UTF8.GetBytes(bytes)), expectedLength);

    [Fact]
    public async Task Data_saver_entry_is_a_miss_under_high()
    {
        // The bug: a track cached at Data saver stayed low quality after the
        // preference went back to High.
        var cache = NewCache();
        await StoreAsync(cache, "vidLow00001", "low", itag: "249", quality: AudioQuality.DataSaver);

        Assert.Null(cache.TryGet("vidLow00001", AudioQuality.High));
        Assert.False(cache.Contains("vidLow00001", AudioQuality.High));
        Assert.NotNull(cache.TryGet("vidLow00001", AudioQuality.DataSaver));
    }

    [Fact]
    public async Task High_entry_serves_under_data_saver()
    {
        // A local file costs no data, so Data saver has no reason to fetch again.
        var cache = NewCache();
        await StoreAsync(cache, "vidHigh0001", "high");

        Assert.True(cache.Contains("vidHigh0001", AudioQuality.DataSaver));
        Assert.Equal("high", File.ReadAllText(cache.TryGet("vidHigh0001", AudioQuality.DataSaver)!.Path));
    }

    [Fact]
    public async Task High_fill_replaces_the_data_saver_entry()
    {
        var cache = NewCache();
        await StoreAsync(cache, "vidSwap0001", "low", itag: "249", quality: AudioQuality.DataSaver);
        await StoreAsync(cache, "vidSwap0001", "high", itag: "251");

        Assert.Equal("high", File.ReadAllText(cache.TryGet("vidSwap0001", AudioQuality.DataSaver)!.Path));
        Assert.Single(Directory.EnumerateFiles(_dir, "vidSwap0001~*"));
    }

    [Theory]
    [InlineData("vidOld00001~251~opus.audio", null)]
    [InlineData("vidOld00001~251~opus~-7.25.audio", -7.25)]
    public void Entries_without_a_tier_token_serve_only_under_data_saver(string name, double? loudness)
    {
        // Written before the tier token existed, so the tier is unknown.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, name), "old");
        var cache = NewCache();

        Assert.Null(cache.TryGet("vidOld00001", AudioQuality.High));
        var hit = cache.TryGet("vidOld00001", AudioQuality.DataSaver);
        Assert.NotNull(hit);
        Assert.Equal("opus", hit.AudioCodec);
        Assert.Equal(loudness, hit.LoudnessDb);
    }

    [Fact]
    public async Task Store_then_get_round_trips_content_and_codec()
    {
        var cache = NewCache();
        await StoreAsync(cache, "vid00000001", "audio-bytes", codec: "mp4a.40.2");

        var hit = cache.TryGet("vid00000001", AudioQuality.High);

        Assert.NotNull(hit);
        Assert.Equal("mp4a.40.2", hit.AudioCodec);
        Assert.Equal("audio-bytes", File.ReadAllText(hit.Path));
    }

    [Fact]
    public void Miss_returns_null()
    {
        Assert.Null(NewCache().TryGet("unknownVid1", AudioQuality.High));
    }

    [Fact]
    public async Task Loudness_round_trips_through_the_filename()
    {
        var cache = NewCache();
        await cache.StoreAsync("vidLoudness", "251", "opus", AudioQuality.High,
            async (file, ct) =>
            {
                await file.WriteAsync(Encoding.UTF8.GetBytes("bytes"), ct);
                return 5L;
            },
            loudnessDb: -9.6);

        var hit = cache.TryGet("vidLoudness", AudioQuality.High);

        Assert.NotNull(hit);
        Assert.Equal(-9.6, hit.LoudnessDb);
        Assert.Equal("opus", hit.AudioCodec);
    }

    [Fact]
    public async Task Entries_without_loudness_token_stay_servable()
    {
        // Both the Stream overload (which never writes loudness) and any file
        // from before the token existed parse to a null loudness.
        var cache = NewCache();
        await StoreAsync(cache, "vidLegacy001", "audio", codec: "mp4a.40.2");

        var hit = cache.TryGet("vidLegacy001", AudioQuality.High);

        Assert.NotNull(hit);
        Assert.Null(hit.LoudnessDb);
        Assert.Equal("mp4a.40.2", hit.AudioCodec);
    }

    [Fact]
    public async Task Remove_deletes_the_entry()
    {
        var cache = NewCache();
        await StoreAsync(cache, "vid00000001", "bytes");

        cache.Remove("vid00000001");

        Assert.Null(cache.TryGet("vid00000001", AudioQuality.High));
        Assert.False(cache.Contains("vid00000001", AudioQuality.High));
    }

    [Fact]
    public async Task Truncated_download_is_not_kept()
    {
        var cache = NewCache();
        await StoreAsync(cache, "vid00000001", "short", expectedLength: 999);

        Assert.Null(cache.TryGet("vid00000001", AudioQuality.High));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Matching_expected_length_is_kept()
    {
        var cache = NewCache();
        await StoreAsync(cache, "vid00000001", "12345", expectedLength: 5);

        Assert.NotNull(cache.TryGet("vid00000001", AudioQuality.High));
    }

    [Fact]
    public async Task Streaming_store_commits_when_written_matches_expected()
    {
        var cache = NewCache();
        await cache.StoreAsync("vid00000001", "251", "opus", AudioQuality.High, async (file, ct) =>
        {
            await file.WriteAsync(Encoding.UTF8.GetBytes("streamed"), ct);
            return 8;
        });

        var hit = cache.TryGet("vid00000001", AudioQuality.High);
        Assert.NotNull(hit);
        Assert.Equal("streamed", File.ReadAllText(hit.Path));
    }

    [Fact]
    public async Task Streaming_store_discards_when_writer_returns_null()
    {
        var cache = NewCache();
        await cache.StoreAsync("vid00000001", "251", "opus", AudioQuality.High, async (file, ct) =>
        {
            await file.WriteAsync(Encoding.UTF8.GetBytes("partial"), ct);
            return null;
        });

        Assert.Null(cache.TryGet("vid00000001", AudioQuality.High));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Streaming_store_discards_on_length_mismatch()
    {
        var cache = NewCache();
        await cache.StoreAsync("vid00000001", "251", "opus", AudioQuality.High, async (file, ct) =>
        {
            await file.WriteAsync(Encoding.UTF8.GetBytes("short"), ct);
            return 999;
        });

        Assert.Null(cache.TryGet("vid00000001", AudioQuality.High));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Streaming_store_cleans_up_when_writer_throws()
    {
        var cache = NewCache();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.StoreAsync("vid00000001", "251", "opus", AudioQuality.High, async (file, ct) =>
            {
                await file.WriteAsync(Encoding.UTF8.GetBytes("doomed"), ct);
                throw new InvalidOperationException("network died");
            }));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Disabled_cache_neither_stores_nor_serves()
    {
        var enabled = NewCache();
        await StoreAsync(enabled, "vid00000001", "bytes");

        var disabled = NewCache(maxBytes: 0);
        Assert.Null(disabled.TryGet("vid00000001", AudioQuality.High));

        await StoreAsync(disabled, "vid00000002", "bytes");
        Assert.Null(NewCache().TryGet("vid00000002", AudioQuality.High));
    }

    [Fact]
    public async Task Eviction_removes_least_recently_used_first()
    {
        // Cap fits two 5-byte entries but not three.
        var cache = NewCache(maxBytes: 12);
        await StoreAsync(cache, "vidAAAAAAAA", "aaaaa");
        await StoreAsync(cache, "vidBBBBBBBB", "bbbbb");

        // Make A the oldest, then touch it via a hit so B becomes the LRU entry.
        SetAge(cache, "vidAAAAAAAA", TimeSpan.FromHours(2));
        SetAge(cache, "vidBBBBBBBB", TimeSpan.FromHours(1));
        Assert.NotNull(cache.TryGet("vidAAAAAAAA", AudioQuality.High));

        await StoreAsync(cache, "vidCCCCCCCC", "ccccc");

        Assert.NotNull(cache.TryGet("vidAAAAAAAA", AudioQuality.High));
        Assert.Null(cache.TryGet("vidBBBBBBBB", AudioQuality.High));
        Assert.NotNull(cache.TryGet("vidCCCCCCCC", AudioQuality.High));
    }

    [Fact]
    public async Task EnforceCap_with_zero_cap_clears_everything()
    {
        var cache = NewCache();
        await StoreAsync(cache, "vidAAAAAAAA", "aaaaa");
        await StoreAsync(cache, "vidBBBBBBBB", "bbbbb");

        cache.MaxSizeBytes = 0;
        cache.EnforceCap();

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Ids_sharing_a_prefix_do_not_collide()
    {
        var cache = NewCache();
        await StoreAsync(cache, "vid1", "one");
        await StoreAsync(cache, "vid1x", "two");

        Assert.Equal("one", File.ReadAllText(cache.TryGet("vid1", AudioQuality.High)!.Path));
        Assert.Equal("two", File.ReadAllText(cache.TryGet("vid1x", AudioQuality.High)!.Path));
    }

    [Fact]
    public async Task Hostile_key_characters_stay_inside_the_cache_directory()
    {
        var cache = NewCache();
        await StoreAsync(cache, "../evil", "bytes", itag: "a/b", codec: "c\\d");

        var file = Assert.Single(Directory.GetFiles(_dir));
        Assert.Equal(Path.GetFullPath(_dir), Path.GetDirectoryName(Path.GetFullPath(file)));
        Assert.NotNull(cache.TryGet("../evil", AudioQuality.High));
    }

    private void SetAge(AudioDiskCache cache, string videoId, TimeSpan age)
    {
        var path = cache.TryGet(videoId, AudioQuality.High)!.Path;
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
    }
}
