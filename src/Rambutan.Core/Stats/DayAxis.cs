namespace Rambutan.Core.Stats;

/// <summary>What a day-axis label names.</summary>
public enum DayLabelKind
{
    /// <summary>The weekday, short.</summary>
    Weekday,

    /// <summary>The day of the month, as a number.</summary>
    DayOfMonth,

    /// <summary>The month, short, under its first day.</summary>
    Month,
}

/// <summary>A label under one bar: the bar's <paramref name="Index"/> in the range
/// and the <paramref name="Day"/> it stands for. The caller writes the text in
/// the user's language.</summary>
public readonly record struct DayLabel(int Index, DayLabelKind Kind, DateOnly Day);

/// <summary>
/// Which bar of a per-day chart gets which label, from the range's length alone,
/// so the labels never need more room than the bars give them.
/// </summary>
public static class DayAxis
{
    /// <summary>The most bars that each get a weekday: a week.</summary>
    public const int WeekdayLabelsUpTo = 7;

    /// <summary>The most bars that get numbers: a month.</summary>
    public const int DayNumberLabelsUpTo = 31;

    /// <summary>Every fifth day, counted back from the last, gets its number.</summary>
    public const int DayNumberStep = 5;

    /// <summary>
    /// The labels for <paramref name="count"/> days from <paramref name="first"/>, in order.
    /// Up to a week, each bar names its weekday. Up to a month, the last bar and
    /// every fifth bar before it give the day of the month (the last is the
    /// newest, so the numbers keep their place as days pass). Longer ranges
    /// name a month under its first day, so a range that starts mid-month has no
    /// label until the next month begins.
    /// </summary>
    public static IReadOnlyList<DayLabel> Labels(DateOnly first, int count)
    {
        var labels = new List<DayLabel>();
        for (var i = 0; i < count; i++)
        {
            var day = first.AddDays(i);
            if (count <= WeekdayLabelsUpTo)
                labels.Add(new DayLabel(i, DayLabelKind.Weekday, day));
            else if (count <= DayNumberLabelsUpTo)
            {
                if ((count - 1 - i) % DayNumberStep == 0)
                    labels.Add(new DayLabel(i, DayLabelKind.DayOfMonth, day));
            }
            else if (day.Day == 1)
                labels.Add(new DayLabel(i, DayLabelKind.Month, day));
        }

        return labels;
    }
}
