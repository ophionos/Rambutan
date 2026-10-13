using Rambutan.Core.Models;
using Rambutan.Core.Stations;

namespace Rambutan.Core.Tests;

public class StationResolverTests
{
    private static TrackInfo Song(string title, string artist, string id) =>
        new() { VideoId = id, Title = title, Artists = [new ArtistRef(artist)] };

    [Fact]
    public async Task Each_track_gets_a_song_its_own_link_or_a_not_found_row()
    {
        var resolver = new StationResolver((query, _) => Task.FromResult<IReadOnlyList<TrackInfo>>(query switch
        {
            "Pulp This Is Hardcore" => [Song("This Is Hardcore", "Pulp", "song1")],
            _ => [Song("Something Else", "Someone", "other")],
        }));

        var rows = await resolver.ResolveAsync(
        [
            new("Pulp", "This Is Hardcore", VideoId: "lf1"),
            new("Holy Ghost!", "Okay", VideoId: "lf2"),
            new("Nobody", "Nothing"),
        ]);

        Assert.Equal([StationMatch.Song, StationMatch.ServiceVideo, StationMatch.NotFound], rows.Select(r => r.Match));
        Assert.Equal("song1", rows[0].Track!.VideoId);
        Assert.Equal("lf2", rows[1].Track!.VideoId);
        Assert.Equal("Okay", rows[1].Track!.Title);
        Assert.Equal("Holy Ghost!", rows[1].Track!.Artists[0].Name);
        Assert.Null(rows[2].Track);
    }

    [Fact]
    public async Task A_track_named_twice_is_searched_and_listed_once()
    {
        var queries = new List<string>();
        var resolver = new StationResolver((query, _) =>
        {
            lock (queries) queries.Add(query);
            return Task.FromResult<IReadOnlyList<TrackInfo>>([]);
        });

        var rows = await resolver.ResolveAsync(
        [
            new("Hot Hot Heat", "Bandages", VideoId: "a"),
            new("Pulp", "Babies", VideoId: "b"),
            new("hot hot heat", "Bandages", VideoId: "c"),
        ]);

        Assert.Equal(["a", "b"], rows.Select(r => r.Track!.VideoId));
        Assert.Equal(2, queries.Count);
    }

    [Fact]
    public async Task Only_the_first_five_results_are_considered()
    {
        IReadOnlyList<TrackInfo> results =
            [.. Enumerable.Range(0, 5).Select(i => Song($"Other {i}", "X", $"o{i}")), Song("Okay", "Holy Ghost!", "late")];
        var rows = await new StationResolver((_, _) => Task.FromResult(results))
            .ResolveAsync([new("Holy Ghost!", "Okay", VideoId: "lf")]);

        Assert.Equal(StationMatch.ServiceVideo, rows[0].Match);
    }

    [Fact]
    public async Task Another_version_plays_only_when_there_is_no_link()
    {
        IReadOnlyList<TrackInfo> live = [Song("That Kiss (Live)", "The Courteeners", "live")];
        var resolver = new StationResolver((_, _) => Task.FromResult(live));

        var rows = await resolver.ResolveAsync(
            [new("The Courteeners", "That Kiss", VideoId: "lf"), new("The Courteeners", "That Kiss ")]);

        Assert.Equal("lf", rows[0].Track!.VideoId);
        // The second names the same track (the space doesn't count), so it isn't listed again.
        Assert.Single(rows);

        rows = await resolver.ResolveAsync([new("The Courteeners", "That Kiss")]);
        Assert.Equal("live", rows[0].Track!.VideoId);
    }

    [Fact]
    public async Task A_failed_search_falls_back_to_the_link()
    {
        var resolver = new StationResolver((query, _) => query.StartsWith("Pulp", StringComparison.Ordinal)
            ? throw new HttpRequestException("reset")
            : Task.FromResult<IReadOnlyList<TrackInfo>>([Song("Okay", "Holy Ghost!", "song")]));

        var rows = await resolver.ResolveAsync(
            [new("Pulp", "Babies", VideoId: "lf"), new("Holy Ghost!", "Okay", VideoId: "lf2")]);

        Assert.Equal(["lf", "song"], rows.Select(r => r.Track!.VideoId));
    }

    [Fact]
    public async Task Every_search_failing_is_a_failure_not_a_list_of_fallbacks()
    {
        var resolver = new StationResolver((_, _) => throw new HttpRequestException("offline"));

        await Assert.ThrowsAsync<HttpRequestException>(() => resolver.ResolveAsync(
            [new("Pulp", "Babies", VideoId: "lf"), new("Holy Ghost!", "Okay", VideoId: "lf2")]));
    }

    [Fact]
    public async Task An_empty_station_resolves_to_no_rows() =>
        Assert.Empty(await new StationResolver((_, _) => throw new InvalidOperationException()).ResolveAsync([]));

    [Fact]
    public async Task Searches_run_at_most_three_at_a_time()
    {
        var running = 0;
        var peak = 0;
        var resolver = new StationResolver(async (_, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
            return [];
        });

        await resolver.ResolveAsync(Enumerable.Range(0, 10).Select(i => new StationTrack("A", $"T{i}", VideoId: "x")).ToList());

        Assert.Equal(3, peak);
    }

    [Fact]
    public async Task Two_stations_resolving_together_share_the_limit()
    {
        var running = 0;
        var peak = 0;
        var resolver = new StationResolver(async (_, _) =>
        {
            InterlockedMax(ref peak, Interlocked.Increment(ref running));
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
            return [];
        });
        List<StationTrack> Station(string name) =>
            Enumerable.Range(0, 6).Select(i => new StationTrack(name, $"T{i}", VideoId: "x")).ToList();

        await Task.WhenAll(resolver.ResolveAsync(Station("A")), resolver.ResolveAsync(Station("B")));

        Assert.Equal(3, peak);
    }

    [Fact]
    public async Task Cancelling_stops_the_resolve()
    {
        using var cts = new CancellationTokenSource();
        var resolver = new StationResolver(async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return [];
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync([new("A", "B", VideoId: "x"), new("C", "D", VideoId: "y")], cts.Token));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
