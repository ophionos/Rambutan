using Rambutan.Core.Stats;

namespace Rambutan.Core.Tests;

public class DayAxisTests
{
    [Fact]
    public void A_week_names_the_weekday_under_every_bar()
    {
        var labels = DayAxis.Labels(new DateOnly(2026, 10, 24), 7);

        Assert.Equal(7, labels.Count);
        Assert.All(labels, l => Assert.Equal(DayLabelKind.Weekday, l.Kind));
        Assert.Equal(Enumerable.Range(0, 7), labels.Select(l => l.Index));
        Assert.Equal(DayOfWeek.Saturday, labels[0].Day.DayOfWeek);
        Assert.Equal(new DateOnly(2026, 10, 30), labels[^1].Day);
    }

    [Fact]
    public void A_month_gives_the_last_bar_and_every_fifth_before_it_its_day_number()
    {
        var labels = DayAxis.Labels(new DateOnly(2026, 10, 1), 30);

        Assert.All(labels, l => Assert.Equal(DayLabelKind.DayOfMonth, l.Kind));
        Assert.Equal([4, 9, 14, 19, 24, 29], labels.Select(l => l.Index));
        Assert.Equal(new DateOnly(2026, 10, 30), labels[^1].Day);
    }

    [Fact]
    public void A_year_names_a_month_under_its_first_day_across_the_year_boundary()
    {
        var labels = DayAxis.Labels(new DateOnly(2025, 10, 31), 365);

        Assert.All(labels, l => Assert.Equal(DayLabelKind.Month, l.Kind));
        Assert.All(labels, l => Assert.Equal(1, l.Day.Day));
        Assert.Equal(12, labels.Count);
        Assert.Equal(new DateOnly(2025, 11, 1), labels[0].Day);
        Assert.Equal(1, labels[0].Index);
        Assert.Equal(new DateOnly(2026, 1, 1), labels[2].Day);
        Assert.Equal(new DateOnly(2026, 10, 1), labels[^1].Day);
    }

    [Fact]
    public void A_range_that_starts_mid_month_has_no_label_before_the_first_new_month()
    {
        var labels = DayAxis.Labels(new DateOnly(2026, 3, 17), 365);

        Assert.Equal(new DateOnly(2026, 4, 1), labels[0].Day);
        Assert.Equal(15, labels[0].Index);
    }

    [Fact]
    public void A_year_whose_first_bar_is_a_first_day_labels_it()
    {
        var labels = DayAxis.Labels(new DateOnly(2025, 11, 1), 365);

        Assert.Equal(0, labels[0].Index);
    }
}
