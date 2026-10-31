using Rambutan.Core.Import;
using Rambutan.Core.Models;
using Rambutan.Core.Stations;

namespace Rambutan.Core.Tests;

public sealed class ImportMatcherTests
{
    private static TrackInfo Song(string title, string artist, string id, int seconds = 200) =>
        new() { VideoId = id, Title = title, Artists = [new ArtistRef(artist)], Duration = TimeSpan.FromSeconds(seconds) };

    private static ImportedTrack Row(string title, string artist) => new(title, [artist]);

    private static ImportMatcher Matcher(
        Func<string, IReadOnlyList<TrackInfo>> search, List<string> asked, StationMatchCache? cache = null) =>
        new((query, _) =>
        {
            asked.Add(query);
            return Task.FromResult(search(query));
        }, cache ?? new StationMatchCache(), TimeSpan.Zero);

    [Fact]
    public async Task Each_distinct_song_is_searched_once_and_only_the_same_recording_matches()
    {
        var asked = new List<string>();
        var matcher = Matcher(query => query switch
        {
            "Van Halen Panama" => [Song("Panama", "Van Halen", "panama1")],
            "Daft Punk One More Time" => [Song("One More Time (Live)", "Daft Punk", "live1")],
            _ => [],
        }, asked);
        var panama = Row("Panama", "Van Halen");

        var matches = await matcher.MatchAsync([panama, Row("One More Time", "Daft Punk"), panama, Row("Panama", "van halen")]);

        Assert.Equal(["Van Halen Panama", "Daft Punk One More Time"], asked);
        Assert.Equal(2, matches.Count);
        Assert.Equal("panama1", matches[ImportMatcher.KeyOf(panama)].Song!.VideoId);
        Assert.Equal(ImportMatch.NotFound, matches[ImportMatcher.KeyOf(Row("One More Time", "Daft Punk"))]);
    }

    [Fact]
    public async Task A_song_without_an_artist_is_searched_by_its_title()
    {
        var asked = new List<string>();

        await Matcher(_ => [], asked).MatchAsync([new ImportedTrack("Untitled", [])]);

        Assert.Equal(["Untitled"], asked);
    }

    [Fact]
    public async Task A_second_import_searches_only_what_is_new_and_takes_the_users_pick()
    {
        var cache = new StationMatchCache();
        var asked = new List<string>();
        var matcher = Matcher(_ => [], asked, cache);
        var known = Row("Panama", "Van Halen");
        await matcher.MatchAsync([known]);
        matcher.Remember(known, Song("Panama", "Van Halen", "picked"));
        asked.Clear();

        var matches = await matcher.MatchAsync([known, Row("Jump", "Van Halen")]);

        Assert.Equal(["Van Halen Jump"], asked);
        Assert.Equal("picked", matches[ImportMatcher.KeyOf(known)].Song!.VideoId);
    }

    [Fact]
    public async Task A_failed_search_is_not_kept_and_after_five_in_a_row_the_rest_are_left()
    {
        var cache = new StationMatchCache();
        var asked = new List<string>();
        var matcher = Matcher(_ => throw new HttpRequestException("offline"), asked, cache);
        var rows = Enumerable.Range(0, 8).Select(i => Row($"T{i}", "A")).ToList();
        var reports = new List<ImportProgress>();

        var matches = await matcher.MatchAsync(rows, new SyncProgress<ImportProgress>(reports.Add));

        Assert.Equal(ImportMatcher.GiveUpAfter, asked.Count);
        Assert.All(rows, r => Assert.Equal(ImportMatch.Failed, matches[ImportMatcher.KeyOf(r)]));
        Assert.False(cache.TryGet(ImportMatcher.Query(rows[0]), out _));
        Assert.Equal(new ImportProgress(0, 8), reports[0]);
        Assert.Equal(new ImportProgress(8, 8), reports[^1]);
    }

    [Fact]
    public async Task A_success_between_failures_starts_the_count_again()
    {
        var asked = new List<string>();
        var matcher = Matcher(query => query.EndsWith('3') ? [] : throw new HttpRequestException("flaky"), asked);

        await matcher.MatchAsync(Enumerable.Range(0, 9).Select(i => Row($"T{i}", "A")));

        Assert.Equal(9, asked.Count);
    }

    [Fact]
    public async Task Cancelling_stops_the_matching()
    {
        using var cts = new CancellationTokenSource();
        var matcher = new ImportMatcher((_, _) =>
        {
            cts.Cancel();
            return Task.FromResult<IReadOnlyList<TrackInfo>>([]);
        }, new StationMatchCache(), TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            matcher.MatchAsync([Row("A", "B"), Row("C", "D")], ct: cts.Token));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public sealed class ImportReviewTests
{
    private static TrackInfo Song(string id) => new() { VideoId = id, Title = id, Artists = [new ArtistRef("A")] };

    private static readonly ImportedTrack Panama = new("Panama", ["Van Halen"]);
    private static readonly ImportedTrack Jump = new("Jump", ["Van Halen"]);
    private static readonly ImportedTrack Lost = new("Lost", ["Nobody"]);
    private static readonly ImportedTrack Unknown = new("Unknown", ["Nobody"]);

    private static ImportReview Review()
    {
        var review = new ImportReview([new ImportedList(ImportedListKind.Playlist, "Rock", [Panama, Jump, Lost, Panama, Unknown])]);
        review.Add(new Dictionary<string, ImportMatch>
        {
            [ImportMatcher.KeyOf(Panama)] = new(ImportMatchState.Found, Song("panama")),
            [ImportMatcher.KeyOf(Jump)] = new(ImportMatchState.Found, Song("jump")),
            [ImportMatcher.KeyOf(Lost)] = ImportMatch.NotFound,
        });
        return review;
    }

    [Fact]
    public void A_list_adds_its_found_songs_in_order_with_duplicates()
    {
        var review = Review();
        var rows = review.Lists[0].Tracks;

        Assert.Equal(["panama", "jump", "panama"], review.SongsOf(rows).Select(s => s.VideoId));
        Assert.Equal(new ImportCounts(Found: 3, NotFound: 1, Failed: 1, LeftOut: 0), review.CountsOf(rows));
        Assert.Equal(ImportMatchState.Failed, review.MatchOf(Unknown).State);
    }

    [Fact]
    public void A_change_is_made_for_the_song_everywhere_and_can_be_undone()
    {
        var review = Review();
        var rows = review.Lists[0].Tracks;
        var changed = new List<string?>();
        review.Changed += changed.Add;

        review.SetLeftOut(Panama, true);
        Assert.Equal(["jump"], review.SongsOf(rows).Select(s => s.VideoId));
        Assert.Equal(2, review.CountsOf(rows).LeftOut);

        review.Choose(Lost, Song("found"));
        Assert.True(review.IsChosen(Lost));
        review.SetLeftOut(Panama, false);
        review.SetLeftOut(Panama, false);

        Assert.Equal(["panama", "jump", "found", "panama"], review.SongsOf(rows).Select(s => s.VideoId));
        Assert.Equal(3, changed.Count);
    }

    [Fact]
    public void Choosing_a_song_puts_a_left_out_one_back()
    {
        var review = Review();
        review.SetLeftOut(Jump, true);

        review.Choose(Jump, Song("other"));

        Assert.False(review.IsLeftOut(Jump));
        Assert.Equal("other", review.SongOf(Jump)!.VideoId);
    }

    [Fact]
    public void The_songs_to_search_again_are_the_failed_and_unsearched_ones_once()
    {
        var review = Review();
        review.Add(new Dictionary<string, ImportMatch> { [ImportMatcher.KeyOf(Jump)] = ImportMatch.Failed });

        Assert.Equal([Jump, Unknown], review.Unsearched(review.Lists));
    }
}

public sealed class ImportPlanTests
{
    private static TrackInfo Song(int n) => new() { VideoId = $"v{n}", Title = $"T{n}", Artists = [] };

    private static List<TrackInfo> Songs(int count) => Enumerable.Range(1, count).Select(Song).ToList();

    [Fact]
    public void A_list_longer_than_the_cap_is_split_into_numbered_parts()
    {
        var parts = ImportPlan.Split("Rock", Songs(7), cap: 3);

        Assert.Equal(["Rock", "Rock (2)", "Rock (3)"], parts.Select(p => p.Name));
        Assert.Equal([3, 3, 1], parts.Select(p => p.Songs.Count));
        Assert.Equal("v4", parts[1].Songs[0].VideoId);
        Assert.Empty(ImportPlan.Split("Rock", [], cap: 3));
        Assert.Single(ImportPlan.Split("Rock", Songs(7), ImportPlan.CapOf(ImportTarget.ThisComputer)));
    }

    private static ImportRecord Record(params int[] counts) => new(
        "Rock", ImportedListKind.Playlist, ImportTarget.YouTubeMusic, "Rock (Spotify)",
        counts.Select((c, i) => new ImportedPart($"PL{i}", c)).ToList(), [], DateTimeOffset.UnixEpoch);

    [Fact]
    public void An_update_fills_the_last_part_then_adds_parts_after_it()
    {
        var (toLast, newParts) = ImportPlan.Append(Record(3, 2), Songs(5), cap: 3);

        Assert.Equal(["v1"], toLast.Select(s => s.VideoId));
        Assert.Equal(["Rock (Spotify) (3)", "Rock (Spotify) (4)"], newParts.Select(p => p.Name));
        Assert.Equal([3, 1], newParts.Select(p => p.Songs.Count));
    }

    [Fact]
    public void An_update_to_a_full_last_part_starts_a_new_one()
    {
        var (toLast, newParts) = ImportPlan.Append(Record(3), Songs(2), cap: 3);

        Assert.Empty(toLast);
        Assert.Equal("Rock (Spotify) (2)", Assert.Single(newParts).Name);
    }

    [Fact]
    public void New_songs_are_the_rows_beyond_what_the_earlier_import_read()
    {
        ImportedTrack a = new("A", ["X"]), b = new("B", ["X"]), c = new("C", ["X"]);
        var record = Record(2) with { Keys = [ImportMatcher.KeyOf(a), ImportMatcher.KeyOf(b), ImportMatcher.KeyOf(b)] };
        var list = new ImportedList(ImportedListKind.Playlist, "Rock", [c, b, a, b, b, a]);

        Assert.Equal([c, b, a], ImportPlan.NewSince(list, record));
    }

    [Fact]
    public void An_import_that_stopped_covers_the_rows_before_the_next_song_to_add()
    {
        ImportedTrack a = new("A", ["X"]), lost = new("Lost", ["X"]), b = new("B", ["X"]), c = new("C", ["X"]);
        IReadOnlyList<ImportedTrack> rows = [a, lost, b, lost, c];
        bool Adds(ImportedTrack t) => t != lost;

        Assert.Equal([lost], ImportPlan.Covered([lost, a], Adds, 0));
        Assert.Equal([a, lost], ImportPlan.Covered(rows, Adds, 1));
        Assert.Equal([a, lost, b, lost], ImportPlan.Covered(rows, Adds, 2));
        Assert.Equal(rows, ImportPlan.Covered(rows, Adds, 3));
    }

    [Fact]
    public void The_earlier_import_is_the_latest_of_the_same_list_and_target()
    {
        var list = new ImportedList(ImportedListKind.Playlist, "Rock", []);
        var old = Record(1);
        var latest = Record(2) with { ImportedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        var local = latest with { Target = ImportTarget.ThisComputer, ImportedAt = DateTimeOffset.UnixEpoch.AddDays(2) };
        var liked = latest with { ListKind = ImportedListKind.LikedSongs, ImportedAt = DateTimeOffset.UnixEpoch.AddDays(3) };

        Assert.Same(latest, ImportPlan.EarlierImport([old, latest, local, liked], list, ImportTarget.YouTubeMusic));
        Assert.Null(ImportPlan.EarlierImport([old], list with { Name = "Pop" }, ImportTarget.YouTubeMusic));
    }

    [Fact]
    public void A_link_list_finds_its_earlier_import_by_its_source_not_its_name()
    {
        const string source = "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M";
        var fromLink = new ImportedList(ImportedListKind.Playlist, "Renamed on Spotify", [], Source: source);
        var fromFile = new ImportedList(ImportedListKind.Playlist, "Rock", []);
        var linkRecord = Record(1) with { Source = source };
        var fileRecord = Record(2) with { ImportedAt = DateTimeOffset.UnixEpoch.AddDays(1) };

        Assert.Same(linkRecord, ImportPlan.EarlierImport([linkRecord, fileRecord], fromLink, ImportTarget.YouTubeMusic));
        Assert.Same(fileRecord, ImportPlan.EarlierImport([linkRecord, fileRecord], fromFile, ImportTarget.YouTubeMusic));
        Assert.Null(ImportPlan.EarlierImport([fileRecord], fromLink, ImportTarget.YouTubeMusic));
        Assert.Null(ImportPlan.EarlierImport([linkRecord], fromFile, ImportTarget.YouTubeMusic));
    }
}
