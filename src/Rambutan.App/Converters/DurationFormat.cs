using System.Globalization;
using Avalonia.Data.Converters;
using Rambutan.Core.Models;
using Rambutan.App.Localization;

namespace Rambutan.App.Converters;

/// <summary>
/// The one way the app writes a length of time. Rows used
/// <c>mm\:ss</c> ("05:38") while the player bar used <c>m\:ss</c> ("5:37"), so
/// the same value showed in two formats at once, and a track over an hour
/// wrapped to minutes in a list.
/// </summary>
public static class DurationFormat
{
    /// <summary>"5:38", "0:07", "1:02:05": a position or a single track.</summary>
    public static string Clock(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;

        var hours = (int)time.TotalHours;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
    }

    /// <summary>"48 min", "1 hr 14 min", "2 hr": the running time of a list.</summary>
    public static string Total(TimeSpan time)
    {
        // Whole minutes, rounded down as YouTube Music's own headers are (a
        // 74:40 album is "1 hour, 14 minutes" there), so the two never disagree.
        var minutes = (int)Math.Max(0, time.TotalMinutes);
        if (minutes == 0 && time > TimeSpan.Zero)
            minutes = 1; // a 20-second list is not "0 min"

        var (h, m) = (minutes / 60, minutes % 60);
        return (h, m) switch
        {
            (0, _) => Strings.Format("duration.minutes", ("minutes", m)),
            (_, 0) => Strings.Format("duration.hours", ("hours", h)),
            _ => Strings.Format("duration.hoursMinutes", ("hours", h), ("minutes", m)),
        };
    }

    /// <summary>
    /// "13 songs • 1 hr 14 min" for a track list. The running time is left out
    /// when any track has no length: a sum of the known ones would understate it.
    /// Empty for an empty list.
    /// </summary>
    public static string Summary(IReadOnlyCollection<TrackInfo> tracks)
    {
        if (tracks.Count == 0)
            return "";

        var count = Strings.Format("duration.songs", ("count", tracks.Count));
        if (tracks.Any(t => t.Duration is not { } d || d <= TimeSpan.Zero))
            return count;

        var total = TimeSpan.FromTicks(tracks.Sum(t => t.Duration!.Value.Ticks));
        return Strings.Format("duration.summary", ("songs", count), ("total", Total(total)));
    }

    /// <summary>Row durations; a missing one binds as blank rather than "0:00".</summary>
    public static readonly IValueConverter Converter =
        new FuncValueConverter<TimeSpan?, string>(d => d is { } time ? Clock(time) : "");
}
