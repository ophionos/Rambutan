using Rambutan.Core.Layout;

namespace Rambutan.Core.Tests;

public class HeroCollapseTests
{
    // A page 500 tall below its title bar: a 216 hero or a 64 bar above the list.
    private const double Area = 500;
    private const double Hero = 216;
    private const double Bar = 64;
    private const double Delta = Hero - Bar;
    private const double Threshold = Delta + HeroCollapse.Slack;

    private static double Viewport(bool collapsed) => Area - (collapsed ? Bar : Hero);

    private static HeroStep Scrolled(bool collapsed, double offset, double extent, bool hold = false) =>
        HeroCollapse.Scrolled(collapsed, offset, extent, Viewport(collapsed), Hero, Bar, hold);

    [Fact]
    public void At_the_top_the_hero_stays_expanded() =>
        Assert.Equal(new HeroStep(false, 0), Scrolled(false, 0, 3000));

    [Fact]
    public void Scrolled_less_than_the_threshold_the_hero_stays_expanded() =>
        Assert.Equal(new HeroStep(false, Threshold - 1), Scrolled(false, Threshold - 1, 3000));

    [Fact]
    public void Scrolled_past_the_threshold_it_collapses_and_the_offset_keeps_the_rows_in_place() =>
        // The list's top moves up by the difference, so the offset does too.
        Assert.Equal(new HeroStep(true, 400 - Delta), Scrolled(false, 400, 3000));

    [Fact]
    public void Collapsed_it_stays_collapsed_until_the_top()
    {
        Assert.Equal(new HeroStep(true, HeroCollapse.Slack), Scrolled(true, HeroCollapse.Slack, 3000));
        Assert.Equal(new HeroStep(true, 2), Scrolled(true, 2, 3000));
        Assert.Equal(new HeroStep(false, HeroCollapse.Top), Scrolled(true, HeroCollapse.Top, 3000));
        Assert.Equal(new HeroStep(false, 0), Scrolled(true, 0, 3000));
    }

    [Fact]
    public void Expanding_leaves_the_offset_as_it_is() =>
        Assert.Equal(0, Scrolled(true, 0, 3000).Offset);

    [Fact]
    public void Between_the_top_and_the_threshold_the_state_holds_either_way()
    {
        // Hysteresis: the same offset keeps whichever state the header is in.
        Assert.False(Scrolled(false, 100, 3000).Collapsed);
        Assert.True(Scrolled(true, 100, 3000).Collapsed);
    }

    [Fact]
    public void A_list_that_fits_with_the_hero_never_collapses() =>
        Assert.False(Scrolled(false, 0, Viewport(false) - 10).Collapsed);

    [Fact]
    public void A_list_that_scrolls_less_than_the_threshold_never_collapses()
    {
        var extent = Viewport(false) + Threshold - 1;
        Assert.False(Scrolled(false, extent - Viewport(false), extent).Collapsed);
    }

    [Fact]
    public void Holding_it_expanded_expands_a_collapsed_hero_and_keeps_it()
    {
        Assert.Equal(new HeroStep(false, 400), Scrolled(true, 400, 3000, hold: true));
        Assert.False(Scrolled(false, 400, 3000, hold: true).Collapsed);
    }

    [Fact]
    public void A_bar_as_tall_as_the_hero_never_takes_its_place() =>
        Assert.False(HeroCollapse.Scrolled(false, 400, 3000, 300, 64, 64, false).Collapsed);

    /// <summary>The list as a window lays it out: each step's state sets the
    /// viewport, the viewport clamps the offset, and the rule sees the result.</summary>
    private static List<bool> Settle(double extent, double offset, bool collapsed, int steps = 12)
    {
        var states = new List<bool> { collapsed };
        for (var i = 0; i < steps; i++)
        {
            var step = Scrolled(collapsed, offset, extent);
            collapsed = step.Collapsed;
            offset = Math.Clamp(step.Offset, 0, Math.Max(0, extent - Viewport(collapsed)));
            states.Add(collapsed);
        }

        return states;
    }

    [Fact]
    public void Content_just_taller_than_the_viewport_never_flips_back_and_forth()
    {
        // Extents round the point where collapsing would make the list fit, and
        // every offset the expanded list can reach.
        for (var extent = Viewport(false) - 5; extent <= Viewport(false) + Threshold + 60; extent += 1)
        {
            var most = Math.Max(0, extent - Viewport(false));
            for (var offset = 0.0; offset <= most; offset += 1)
            {
                var states = Settle(extent, offset, collapsed: false);
                var changes = states.Zip(states.Skip(1)).Count(pair => pair.First != pair.Second);
                Assert.True(changes <= 1, $"extent {extent}, offset {offset}: {changes} changes");
            }
        }
    }

    [Fact]
    public void A_collapse_never_lands_at_an_offset_that_expands_it_again()
    {
        for (var extent = Viewport(false) + Threshold; extent <= Viewport(false) + Threshold + 200; extent += 7)
        {
            var step = Scrolled(false, extent - Viewport(false), extent);
            Assert.True(step.Collapsed);
            var room = extent - Viewport(true);
            Assert.True(step.Offset <= room + 0.001, $"extent {extent}: {step.Offset} past {room}");
            Assert.True(step.Offset > HeroCollapse.Top);
        }
    }

    [Fact]
    public void Restored_to_a_scrolled_offset_it_shows_collapsed_at_that_offset() =>
        Assert.Equal(new HeroStep(true, 300),
            HeroCollapse.Restored(300, 3000, Viewport(false), Hero, Bar, false));

    [Fact]
    public void Restored_at_the_top_it_shows_expanded() =>
        Assert.Equal(new HeroStep(false, 0),
            HeroCollapse.Restored(0, 3000, Viewport(false), Hero, Bar, false));

    [Fact]
    public void Restored_where_the_collapsed_list_cannot_hold_the_offset_it_stays_expanded()
    {
        // Collapsed, this list scrolls only 20: an offset of 40 would be cut short.
        var extent = Viewport(true) + 20;
        Assert.Equal(new HeroStep(false, 40),
            HeroCollapse.Restored(40, extent, Viewport(false), Hero, Bar, false));
    }

    [Fact]
    public void Restored_while_held_it_stays_expanded() =>
        Assert.False(HeroCollapse.Restored(300, 3000, Viewport(false), Hero, Bar, true).Collapsed);

    [Fact]
    public void A_restored_collapse_is_stable()
    {
        var restored = HeroCollapse.Restored(30, 3000, Viewport(false), Hero, Bar, false);
        Assert.True(restored.Collapsed);
        Assert.Equal(restored, Scrolled(true, restored.Offset, 3000));
    }
}
