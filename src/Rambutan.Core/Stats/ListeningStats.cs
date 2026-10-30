using Rambutan.Core.Models;

namespace Rambutan.Core.Stats;

public sealed record RankedSong(PlayRecord Track, int Plays, TimeSpan Listened);

public sealed record RankedArtist(ArtistRef Artist, int Plays, TimeSpan Listened);

public sealed record RankedAlbum(
    string Title, string? BrowseId, string? Artist, string? ThumbnailUrl, int Plays, TimeSpan Listened);

/// <summary>What the stats show for one period, ending today.</summary>
/// <param name="First">The period's first day; <paramref name="PerDay"/> starts there.</param>
/// <param name="PreviousListened">The same number of days just before <paramref name="First"/>.</param>
/// <param name="Heatmap">Time per weekday and hour in the period, at
/// <c>(int)DayOfWeek * 24 + hour</c>.</param>
/// <param name="CurrentStreak">Days in a row with listening up to today, or up to
/// yesterday while today has none yet: today counts until midnight.</param>
/// <param name="LongestStreak">Over the whole log, not only the period.</param>
/// <param name="Plays">Plays in the period that count (<see cref="PlayRules.CountsAsPlay"/>).</param>
/// <param name="NewSongs">Songs first played in the period, the latest first;
/// <paramref name="NewSongCount"/> counts them all.</param>
public sealed record ListeningSummary(
    DateOnly First,
    DateOnly Last,
    TimeSpan Listened,
    TimeSpan PreviousListened,
    IReadOnlyList<TimeSpan> PerDay,
    IReadOnlyList<TimeSpan> Heatmap,
    int CurrentStreak,
    int LongestStreak,
    int Plays,
    IReadOnlyList<RankedSong> TopSongs,
    IReadOnlyList<RankedArtist> TopArtists,
    IReadOnlyList<RankedAlbum> TopAlbums,
    IReadOnlyList<RankedSong> NewSongs,
    int NewSongCount);

/// <summary>
/// Listening stats from the play log, calculated on each visit. Periods roll:
/// the last <c>days</c> days ending today, compared with as many days before
/// them. A play's time is spread over the hours it ran from its start, in the
/// user's time zone; it counts in the top lists on the day it started.
/// </summary>
public static class ListeningStats
{
    public const int WeekDays = 7;
    public const int MonthDays = 30;
    public const int YearDays = 365;

    public static ListeningSummary Calculate(
        IReadOnlyList<PlayRecord> log, int days, DateTimeOffset now, TimeZoneInfo zone, int top = 10)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);

        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var first = last.AddDays(1 - days);
        var previousFirst = first.AddDays(-days);

        var perDay = new TimeSpan[days];
        var heatmap = new TimeSpan[7 * 24];
        var listened = TimeSpan.Zero;
        var previous = TimeSpan.Zero;
        var daysWithListening = new HashSet<DateOnly>();

        foreach (var play in log)
        {
            Spread(play, zone, (at, amount) =>
            {
                var day = DateOnly.FromDateTime(at);
                daysWithListening.Add(day);
                if (day >= first && day <= last)
                {
                    listened += amount;
                    perDay[day.DayNumber - first.DayNumber] += amount;
                    heatmap[(int)at.DayOfWeek * 24 + at.Hour] += amount;
                }
                else if (day >= previousFirst && day < first)
                    previous += amount;
            });
        }

        var counted = log
            .Where(p => PlayRules.CountsAsPlay(p) && !PlayRules.IsTimeOnly(p))
            .OrderBy(p => p.StartedAt)
            .ToList();
        var inPeriod = counted
            .Where(p => StartDay(p, zone) is var day && day >= first && day <= last)
            .ToList();

        var songs = inPeriod
            .GroupBy(p => p.SongKey)
            .Select(g => new RankedSong(g.Last(), g.Count(), Sum(g)))
            .ToList();
        var bySong = songs.ToDictionary(s => s.Track.SongKey);

        var firstPlays = counted
            .GroupBy(p => p.SongKey)
            .Select(g => g.First())
            .Where(p => StartDay(p, zone) is var day && day >= first && day <= last)
            .OrderByDescending(p => p.StartedAt)
            .ToList();

        return new ListeningSummary(
            first, last, listened, previous, perDay, heatmap,
            CurrentStreak(daysWithListening, last),
            LongestStreak(daysWithListening),
            inPeriod.Count,
            Rank(songs, s => s.Plays, s => s.Listened, s => s.Track.Title).Take(top).ToList(),
            Rank(Artists(inPeriod), a => a.Plays, a => a.Listened, a => a.Artist.Name).Take(top).ToList(),
            Rank(Albums(inPeriod), a => a.Plays, a => a.Listened, a => a.Title).Take(top).ToList(),
            [.. firstPlays.Take(top).Select(p => bySong[p.SongKey])],
            firstPlays.Count);
    }

    /// <summary>Hands out the play's time hour by hour, in local time, assuming it
    /// ran without a break from its start: a pause moves time later than it was
    /// heard, which the stats accept.</summary>
    private static void Spread(PlayRecord play, TimeZoneInfo zone, Action<DateTime, TimeSpan> add)
    {
        var at = play.StartedAt;
        var left = play.Listened;
        while (left > TimeSpan.Zero)
        {
            var local = TimeZoneInfo.ConvertTime(at, zone);
            var intoHour = TimeSpan.FromTicks(local.TimeOfDay.Ticks % TimeSpan.TicksPerHour);
            var chunk = TimeSpan.FromHours(1) - intoHour;
            if (chunk > left)
                chunk = left;
            add(local.DateTime, chunk);
            at += chunk;
            left -= chunk;
        }
    }

    private static DateOnly StartDay(PlayRecord play, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(play.StartedAt, zone).DateTime);

    private static TimeSpan Sum(IEnumerable<PlayRecord> plays) =>
        plays.Aggregate(TimeSpan.Zero, (sum, p) => sum + p.Listened);

    private static IEnumerable<T> Rank<T>(
        IEnumerable<T> items, Func<T, int> plays, Func<T, TimeSpan> listened, Func<T, string> name) =>
        items.OrderByDescending(plays).ThenByDescending(listened).ThenBy(name, StringComparer.CurrentCultureIgnoreCase);

    private static IEnumerable<RankedArtist> Artists(IReadOnlyList<PlayRecord> plays) => plays
        .SelectMany(p => p.Artists.DistinctBy(ArtistKey).Select(a => (Artist: a, Play: p)))
        .GroupBy(x => ArtistKey(x.Artist))
        .Select(g => new RankedArtist(g.Last().Artist, g.Count(), Sum(g.Select(x => x.Play))));

    private static string ArtistKey(ArtistRef artist) =>
        artist.BrowseId ?? "name:" + artist.Name.Trim().ToUpperInvariant();

    private static IEnumerable<RankedAlbum> Albums(IReadOnlyList<PlayRecord> plays) => plays
        .Where(p => !string.IsNullOrWhiteSpace(p.Album))
        .GroupBy(p => p.AlbumBrowseId
            ?? "name:" + p.Album!.Trim().ToUpperInvariant() + "|" + p.Artists.FirstOrDefault()?.Name.Trim().ToUpperInvariant())
        .Select(g =>
        {
            var latest = g.Last();
            return new RankedAlbum(latest.Album!, latest.AlbumBrowseId, latest.Artists.FirstOrDefault()?.Name,
                latest.ThumbnailUrl, g.Count(), Sum(g));
        });

    private static int CurrentStreak(HashSet<DateOnly> days, DateOnly today)
    {
        var day = days.Contains(today) ? today : today.AddDays(-1);
        var streak = 0;
        while (days.Contains(day))
        {
            streak++;
            day = day.AddDays(-1);
        }
        return streak;
    }

    private static int LongestStreak(HashSet<DateOnly> days)
    {
        var longest = 0;
        foreach (var day in days)
        {
            if (days.Contains(day.AddDays(-1)))
                continue;
            var length = 1;
            while (days.Contains(day.AddDays(length)))
                length++;
            longest = Math.Max(longest, length);
        }
        return longest;
    }
}
