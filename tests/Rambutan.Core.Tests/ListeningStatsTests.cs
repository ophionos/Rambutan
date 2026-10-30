using Rambutan.Core.Models;
using Rambutan.Core.Stats;

namespace Rambutan.Core.Tests;

public class ListeningStatsTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // Friday 30 October 2026, noon.
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static int _ids;

    private static PlayRecord Play(
        DateTimeOffset at, double minutes, string id = "aaaaaaaaaaa", string title = "Song",
        string artist = "Artist", string? album = null, double? durationMinutes = 4, string? songId = null) => new()
    {
        Id = (++_ids).ToString(),
        StartedAt = at,
        Listened = TimeSpan.FromMinutes(minutes),
        VideoId = id,
        SongId = songId,
        Title = title,
        Artists = [new ArtistRef(artist, "UC" + artist)],
        Album = album,
        Duration = durationMinutes is { } d ? TimeSpan.FromMinutes(d) : null,
    };

    private static DateTimeOffset Day(int month, int day, int hour = 12) => new(2026, month, day, hour, 0, 0, TimeSpan.Zero);

    private static ListeningSummary Week(params PlayRecord[] log) =>
        ListeningStats.Calculate(log, ListeningStats.WeekDays, Now, Utc);

    [Fact]
    public void The_period_ends_today_and_is_compared_with_as_many_days_before_it()
    {
        var stats = Week(
            Play(Day(10, 29), 10),
            Play(Day(10, 24), 2),   // first day of the week
            Play(Day(10, 23), 5),   // last day of the week before
            Play(Day(10, 17), 1),   // first day of the week before
            Play(Day(10, 16), 3));  // before both

        Assert.Equal(new DateOnly(2026, 10, 24), stats.First);
        Assert.Equal(new DateOnly(2026, 10, 30), stats.Last);
        Assert.Equal(TimeSpan.FromMinutes(12), stats.Listened);
        Assert.Equal(TimeSpan.FromMinutes(6), stats.PreviousListened);
        Assert.Equal(7, stats.PerDay.Count);
        Assert.Equal(TimeSpan.FromMinutes(2), stats.PerDay[0]);
        Assert.Equal(TimeSpan.FromMinutes(10), stats.PerDay[5]);
    }

    [Fact]
    public void A_play_over_midnight_is_spread_over_both_days_and_hours()
    {
        var stats = Week(Play(new DateTimeOffset(2026, 10, 28, 23, 50, 0, TimeSpan.Zero), 20, durationMinutes: 25));

        Assert.Equal(TimeSpan.FromMinutes(10), stats.PerDay[4]);
        Assert.Equal(TimeSpan.FromMinutes(10), stats.PerDay[5]);
        Assert.Equal(TimeSpan.FromMinutes(10), stats.Heatmap[(int)DayOfWeek.Wednesday * 24 + 23]);
        Assert.Equal(TimeSpan.FromMinutes(10), stats.Heatmap[(int)DayOfWeek.Thursday * 24 + 0]);
    }

    [Fact]
    public void Days_and_hours_are_the_users_own()
    {
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("test+9", TimeSpan.FromHours(9), "test+9", "test+9");

        // 20:00 UTC on Thursday is 05:00 on Friday in UTC+9.
        var stats = ListeningStats.Calculate(
            [Play(Day(10, 29, 20), 30)], ListeningStats.WeekDays, Now, tokyo);

        Assert.Equal(new DateOnly(2026, 10, 30), stats.Last);
        Assert.Equal(TimeSpan.FromMinutes(30), stats.PerDay[^1]);
        Assert.Equal(TimeSpan.FromMinutes(30), stats.Heatmap[(int)DayOfWeek.Friday * 24 + 5]);
    }

    [Fact]
    public void Today_counts_until_midnight_in_the_current_streak()
    {
        PlayRecord[] log = [Play(Day(10, 28), 1), Play(Day(10, 29), 1)];
        Assert.Equal(2, Week(log).CurrentStreak);

        Assert.Equal(3, Week([.. log, Play(Day(10, 30, 8), 1)]).CurrentStreak);
        Assert.Equal(0, Week(Play(Day(10, 27), 1)).CurrentStreak);
    }

    [Fact]
    public void The_longest_streak_reaches_back_past_the_period()
    {
        var stats = Week(
            Play(Day(9, 1), 1), Play(Day(9, 2), 1), Play(Day(9, 3), 1), Play(Day(9, 4), 1),
            Play(Day(10, 29), 1), Play(Day(10, 30), 1));

        Assert.Equal(2, stats.CurrentStreak);
        Assert.Equal(4, stats.LongestStreak);
    }

    [Fact]
    public void A_streak_holds_across_the_change_from_summer_time()
    {
        // Central Europe leaves summer time at 03:00 on Sunday 25 October 2026.
        var cet = TimeZoneInfo.CreateCustomTimeZone("test-cet", TimeSpan.FromHours(1), "test-cet", "test-cet", "test-cest",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 29),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 25)),
        ]);
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        // 22:30 UTC on the 24th is 00:30 on the 25th in summer time; 23:30 UTC
        // on the 25th is 00:30 on the 26th in winter time.
        var stats = ListeningStats.Calculate(
        [
            Play(new DateTimeOffset(2026, 10, 23, 22, 30, 0, TimeSpan.Zero), 1),
            Play(new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero), 1),
            Play(new DateTimeOffset(2026, 10, 25, 23, 30, 0, TimeSpan.Zero), 1),
        ], ListeningStats.WeekDays, now, cet);

        Assert.Equal(3, stats.CurrentStreak);
        Assert.Equal(TimeSpan.FromMinutes(1), stats.PerDay[^1]);
    }

    [Fact]
    public void A_long_video_counts_as_time_but_never_as_a_song_or_artist()
    {
        var stats = Week(
            Play(Day(10, 29), 40, id: "stream00000", title: "Live", artist: "Streamer", durationMinutes: 60),
            Play(Day(10, 29, 14), 50, id: "live0000000", title: "No length", artist: "Streamer", durationMinutes: null),
            Play(Day(10, 29, 16), 4, title: "Song", artist: "Band"));

        Assert.Equal(TimeSpan.FromMinutes(94), stats.Listened);
        Assert.Equal(["Song"], stats.TopSongs.Select(s => s.Track.Title));
        Assert.Equal(["Band"], stats.TopArtists.Select(a => a.Artist.Name));
        Assert.Equal(1, stats.Plays);
    }

    [Fact]
    public void A_local_file_counts_as_a_song_at_any_length()
    {
        var stats = Week(Play(Day(10, 29), 70, id: "local:12", title: "Whole album side", artist: "Band", durationMinutes: 80));

        Assert.Equal(["Whole album side"], stats.TopSongs.Select(s => s.Track.Title));
    }

    [Theory]
    [InlineData(20, 3.0, false)] // 20 s of a three-minute song
    [InlineData(30, 3.0, true)]  // 30 s of it
    [InlineData(20, 0.5, true)]  // half of a 30-second song
    [InlineData(20, null, false)]
    public void A_play_counts_after_30_seconds_or_half_the_track(int seconds, double? durationMinutes, bool counts)
    {
        var play = Play(Day(10, 29), seconds / 60.0, durationMinutes: durationMinutes);

        Assert.Equal(counts, PlayRules.CountsAsPlay(play));
        Assert.Equal(counts ? 1 : 0, Week(play).TopSongs.Count);
        Assert.Equal(play.Listened, Week(play).Listened);
    }

    [Fact]
    public void Songs_rank_by_plays_then_time_and_a_video_counts_with_its_audio_track()
    {
        var stats = Week(
            Play(Day(10, 25), 3, id: "video000000", songId: "audio000000", title: "Song A (video)"),
            Play(Day(10, 26), 3, id: "audio000000", songId: "audio000000", title: "Song A"),
            Play(Day(10, 27), 4, id: "bbbbbbbbbbb", title: "Song B"),
            Play(Day(10, 28), 4, id: "bbbbbbbbbbb", title: "Song B"),
            Play(Day(10, 29), 9, id: "ccccccccccc", title: "Song C"));

        Assert.Equal(["Song B", "Song A", "Song C"], stats.TopSongs.Select(s => s.Track.Title));
        Assert.Equal([2, 2, 1], stats.TopSongs.Select(s => s.Plays));
    }

    [Fact]
    public void An_artist_counts_once_per_play_and_albums_group_by_name_without_a_link()
    {
        var duet = Play(Day(10, 29), 3, title: "Duet") with
        {
            Artists = [new("One", "UC1"), new("Two", "UC2"), new("One", "UC1")],
            Album = "Shared",
        };
        var stats = Week(
            duet,
            Play(Day(10, 28), 3, id: "local:1", title: "Solo", artist: "Two", album: "Shared") with
            {
                Artists = [new("Two")],
            },
            Play(Day(10, 27), 3, id: "local:2", title: "Other", artist: "Two", album: "shared ") with
            {
                Artists = [new("two")],
            });

        Assert.Equal([("Two", 2), ("One", 1), ("Two", 1)],
            stats.TopArtists.Select(a => (a.Artist.Name, a.Plays)));
        Assert.Equal([("Shared", 2), ("Shared", 1)], stats.TopAlbums.Select(a => (a.Title, a.Plays)));
    }

    [Fact]
    public void New_songs_are_those_first_played_in_the_period()
    {
        var stats = Week(
            Play(Day(9, 1), 3, id: "olderrrrrrr", title: "Old favourite"),
            Play(Day(10, 29), 3, id: "olderrrrrrr", title: "Old favourite"),
            Play(Day(10, 26), 3, id: "newwwwwwwww", title: "Found Monday"),
            Play(Day(10, 29), 3, id: "newwwwwwwww", title: "Found Monday"),
            Play(Day(10, 28), 3, id: "neweeeeeeee", title: "Found Wednesday"),
            // Only a skip before: its first play that counts is this week.
            Play(Day(9, 2), 0.1, id: "skippedddd0", title: "Skipped once"),
            Play(Day(10, 27), 3, id: "skippedddd0", title: "Skipped once"));

        Assert.Equal(["Found Wednesday", "Skipped once", "Found Monday"], stats.NewSongs.Select(s => s.Track.Title));
        Assert.Equal(2, stats.NewSongs[^1].Plays);
        Assert.Equal(3, stats.NewSongCount);
    }

    [Fact]
    public void An_empty_log_is_an_empty_period()
    {
        var stats = Week();

        Assert.Equal(TimeSpan.Zero, stats.Listened);
        Assert.All(stats.PerDay, d => Assert.Equal(TimeSpan.Zero, d));
        Assert.Equal(0, stats.CurrentStreak);
        Assert.Empty(stats.TopSongs);
    }

    [Fact]
    public void A_record_of_a_local_file_keeps_no_links_or_artwork()
    {
        var track = new TrackInfo
        {
            VideoId = "local:3",
            Title = "Tagged",
            Artists = [new("Band", "UCmatched")],
            Album = "Tags",
            AlbumBrowseId = "MPREb_matched",
            SongId = "matched0000",
            ThumbnailUrl = "https://example.invalid/cover.jpg",
        };

        var record = PlayRecord.From("1", track, Now, TimeSpan.FromMinutes(1));

        Assert.Null(record.ThumbnailUrl);
        Assert.Null(record.SongId);
        Assert.Null(record.AlbumBrowseId);
        Assert.Null(record.Artists[0].BrowseId);
        Assert.Equal("Tags", record.Album);
    }
}
