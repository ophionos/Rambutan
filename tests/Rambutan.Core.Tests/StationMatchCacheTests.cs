using Rambutan.Core.Models;
using Rambutan.Core.Stations;

namespace Rambutan.Core.Tests;

public class StationMatchCacheTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TrackInfo Song(string title, string artist, string id) =>
        new() { VideoId = id, Title = title, Artists = [new ArtistRef(artist)] };

    private readonly ManualTime _time = new();
    private int _searches;

    private StationResolver Resolver(StationMatchCache cache, Func<string, IReadOnlyList<TrackInfo>> answer) =>
        new((query, _) =>
        {
            Interlocked.Increment(ref _searches);
            return Task.FromResult(answer(query));
        }, cache: cache);

    [Fact]
    public async Task A_track_searched_before_is_not_searched_again()
    {
        var cache = new StationMatchCache(_time);
        var resolver = Resolver(cache, _ => [Song("Okay", "Holy Ghost!", "song")]);

        await resolver.ResolveAsync([new("Holy Ghost!", "Okay")]);
        var rows = await resolver.ResolveAsync([new("HOLY GHOST!", "Okay ")]);

        Assert.Equal(1, _searches);
        Assert.Equal("song", rows[0].Track!.VideoId);
    }

    [Fact]
    public async Task A_kept_outcome_is_applied_to_the_new_sample()
    {
        // The first sample had a link, so the remix wasn't taken; the next has
        // none, and the remix kept from the same search is the last resort.
        var cache = new StationMatchCache(_time);
        var resolver = Resolver(cache, _ => [Song("That Kiss (Live)", "The Courteeners", "live")]);

        var first = await resolver.ResolveAsync([new("The Courteeners", "That Kiss", VideoId: "lf")]);
        var second = await resolver.ResolveAsync([new("The Courteeners", "That Kiss")]);

        Assert.Equal(StationMatch.ServiceVideo, first[0].Match);
        Assert.Equal("live", second[0].Track!.VideoId);
        Assert.Equal(1, _searches);
    }

    [Fact]
    public async Task No_song_is_searched_again_sooner_than_a_song()
    {
        var cache = new StationMatchCache(_time);
        var resolver = Resolver(cache, query => query.StartsWith("Pulp", StringComparison.Ordinal)
            ? [Song("Babies", "Pulp", "song")]
            : []);
        StationTrack[] station = [new("Pulp", "Babies"), new("Nobody", "Nothing", VideoId: "lf")];

        await resolver.ResolveAsync(station);
        _time.Now += StationMatchCache.NoSongLifetime + TimeSpan.FromMinutes(1);
        await resolver.ResolveAsync(station);
        Assert.Equal(3, _searches);

        _time.Now += StationMatchCache.SongLifetime;
        await resolver.ResolveAsync(station);
        Assert.Equal(5, _searches);
    }

    [Fact]
    public async Task A_failed_search_is_not_kept()
    {
        var cache = new StationMatchCache(_time);
        var fail = true;
        var resolver = new StationResolver((query, _) => fail && query.StartsWith("Pulp", StringComparison.Ordinal)
            ? throw new HttpRequestException("reset")
            : Task.FromResult<IReadOnlyList<TrackInfo>>([Song("Babies", "Pulp", "song"), Song("Okay", "Holy Ghost!", "ok")]),
            cache: cache);
        StationTrack[] station = [new("Pulp", "Babies", VideoId: "lf"), new("Holy Ghost!", "Okay")];

        var first = await resolver.ResolveAsync(station);
        fail = false;
        var second = await resolver.ResolveAsync(station);

        Assert.Equal("lf", first[0].Track!.VideoId);
        Assert.Equal("song", second[0].Track!.VideoId);
    }

    [Fact]
    public async Task Every_search_failing_still_fails_when_others_came_from_the_cache()
    {
        var cache = new StationMatchCache(_time);
        var offline = false;
        var resolver = new StationResolver((_, _) => offline
            ? throw new HttpRequestException("offline")
            : Task.FromResult<IReadOnlyList<TrackInfo>>([Song("Okay", "Holy Ghost!", "ok")]), cache: cache);

        await resolver.ResolveAsync([new("Holy Ghost!", "Okay")]);
        offline = true;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            resolver.ResolveAsync([new("Holy Ghost!", "Okay"), new("Pulp", "Babies", VideoId: "lf")]));
    }

    [Fact]
    public async Task A_station_answered_from_the_cache_needs_no_search()
    {
        var cache = new StationMatchCache(_time);
        var offline = false;
        var resolver = new StationResolver((_, _) => offline
            ? throw new HttpRequestException("offline")
            : Task.FromResult<IReadOnlyList<TrackInfo>>([Song("Okay", "Holy Ghost!", "ok")]), cache: cache);

        await resolver.ResolveAsync([new("Holy Ghost!", "Okay")]);
        offline = true;

        var rows = await resolver.ResolveAsync([new("Holy Ghost!", "Okay")]);
        Assert.Equal("ok", rows[0].Track!.VideoId);
    }

    [Fact]
    public void Export_keeps_the_newest_current_outcomes_and_import_skips_expired_ones()
    {
        var cache = new StationMatchCache(_time);
        for (var i = 0; i < StationMatchCache.MaxEntries + 5; i++)
            cache.Put(new StationTrack("A", $"T{i}"),
                new StationSearchOutcome(Song($"T{i}", "A", $"s{i}"), null, _time.Now.AddSeconds(-i)));
        cache.Put(new StationTrack("B", "Old"),
            new StationSearchOutcome(null, null, _time.Now - StationMatchCache.NoSongLifetime - TimeSpan.FromHours(1)));

        var saved = cache.Export();
        Assert.Equal(StationMatchCache.MaxEntries, saved.Count);
        Assert.Equal(StationMatchCache.KeyOf(new StationTrack("A", "T0")), saved[0].Key);

        var restored = new StationMatchCache(_time);
        restored.Import([.. saved, new(StationMatchCache.KeyOf(new StationTrack("B", "Old")),
            new StationSearchOutcome(null, null, _time.Now - TimeSpan.FromDays(10)))]);
        Assert.True(restored.TryGet(new StationTrack("a", "t0"), out var hit));
        Assert.Equal("s0", hit.Song!.VideoId);
        Assert.False(restored.TryGet(new StationTrack("B", "Old"), out _));
    }

    [Fact]
    public void An_outcome_dated_in_the_future_is_not_trusted()
    {
        var cache = new StationMatchCache(_time);
        cache.Put(new StationTrack("A", "B"), new StationSearchOutcome(Song("B", "A", "s"), null, _time.Now.AddDays(1)));
        Assert.False(cache.TryGet(new StationTrack("A", "B"), out _));
    }
}
