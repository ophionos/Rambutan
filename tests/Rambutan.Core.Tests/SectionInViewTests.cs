using Rambutan.Core.Layout;

namespace Rambutan.Core.Tests;

public class SectionInViewTests
{
    // Four sections: headings at 0, 300, 700 and 1000; content 1200 tall.
    private static readonly double[] Tops = [0, 300, 700, 1000];
    private const double Viewport = 400;
    private const double Extent = 1200;

    [Fact]
    public void At_the_top_the_first_section_is_in_view() =>
        Assert.Equal(0, SectionInView.Index(Tops, 0, Viewport, Extent));

    [Fact]
    public void Before_the_first_heading_the_first_section_is_in_view() =>
        Assert.Equal(0, SectionInView.Index([40, 300, 700, 1000], 10, Viewport, Extent));

    [Fact]
    public void In_the_middle_the_last_heading_above_the_top_wins() =>
        Assert.Equal(1, SectionInView.Index(Tops, 500, Viewport, Extent));

    [Fact]
    public void A_heading_exactly_at_the_top_is_in_view() =>
        Assert.Equal(2, SectionInView.Index(Tops, 700, Viewport, Extent));

    [Fact]
    public void A_heading_just_under_the_top_counts_within_the_margin() =>
        Assert.Equal(2, SectionInView.Index(Tops, 700 - SectionInView.TopMargin, Viewport, Extent));

    [Fact]
    public void A_heading_past_the_margin_does_not_count_yet() =>
        Assert.Equal(1, SectionInView.Index(Tops, 700 - SectionInView.TopMargin - 1, Viewport, Extent));

    [Fact]
    public void At_the_end_of_the_scroll_a_short_last_section_is_in_view()
    {
        // The last heading (1000) can never reach the top: the scroll ends at 800.
        Assert.Equal(3, SectionInView.Index(Tops, Extent - Viewport, Viewport, Extent));
        Assert.Equal(2, SectionInView.Index(Tops, Extent - Viewport - 50, Viewport, Extent));
    }

    [Fact]
    public void No_headings_is_no_section() =>
        Assert.Equal(-1, SectionInView.Index([], 100, Viewport, Extent));

    [Fact]
    public void One_heading_is_always_in_view()
    {
        Assert.Equal(0, SectionInView.Index([200], 0, Viewport, Extent));
        Assert.Equal(0, SectionInView.Index([200], 800, Viewport, Extent));
    }

    [Fact]
    public void Content_that_fits_marks_the_first_section() =>
        Assert.Equal(0, SectionInView.Index(Tops, 0, 1500, Extent));
}
