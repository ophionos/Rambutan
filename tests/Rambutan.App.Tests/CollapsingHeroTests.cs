using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;
using Rambutan.Core.Layout;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>A playlist page's hero gives way to a thin bar once its list is
/// scrolled, and comes back at the top.</summary>
public class CollapsingHeroTests
{
    private static TrackInfo Track(int i) => new()
    {
        VideoId = $"v{i:D4}", Title = $"Song {i}", Artists = [new ArtistRef("Artist")],
    };

    private sealed class Rig
    {
        public required Window Window { get; init; }
        public required Views.PlaylistPage View { get; init; }
        public required PlaylistPageViewModel Page { get; init; }
        public required ListBox List { get; init; }
        public required CollapsingHero Hero { get; init; }

        public ScrollViewer Scroll => (ScrollViewer)List.Scroll!;

        public ActionRow HeroRow => Hero.Hero!.GetVisualDescendants().OfType<ActionRow>().Single();

        public Button HeroPlay => HeroRow.Items.OfType<Button>().Single(ActionRow.GetKeep);

        public Button HeroShuffle => (Button)HeroRow.Items[1];

        public bool ShowsHero => Hero.Hero!.IsEffectivelyVisible;

        public bool ShowsBar => Hero.Bar.IsEffectivelyVisible;

        public IInputElement? Focused => Window.FocusManager!.GetFocusedElement();

        public void ScrollTo(double y)
        {
            Scroll.Offset = new Vector(0, y);
            Settle(Window);
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static Rig Show(int count = 120, double saved = 0, bool reducedMotion = false)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var catalog = new FakeCatalog
        {
            Playlist = new PlaylistPage
            {
                PlaylistId = "PL1", Title = "Road trip",
                Tracks = Enumerable.Range(0, count).Select(Track).ToList(),
            },
        };
        var page = new PlaylistPageViewModel(catalog, null!, null!, null!, "PL1");
        page.LoadAsync().GetAwaiter().GetResult();
        page.SavedScrollOffset = saved;
        var view = new Views.PlaylistPage { DataContext = page };
        var window = new Window { Width = 760, Height = 560, DataContext = new MainViewModel(), Content = view };
        if (reducedMotion)
            window.Classes.Add("reducedmotion");
        window.Show();
        Settle(window);
        return new Rig
        {
            Window = window,
            View = view,
            Page = page,
            List = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TrackList"),
            Hero = view.GetVisualDescendants().OfType<CollapsingHero>().Single(),
        };
    }

    private static bool Fades(Control part) =>
        part.Transitions is { Count: > 0 } transitions &&
        transitions.Any(t => t is DoubleTransition { Property: var p } && p == Visual.OpacityProperty);

    [Fact]
    public void AtTheTop_TheHeroShowsAndTheBarDoesNot() => Headless.Run(() =>
    {
        var rig = Show();

        Assert.False(rig.Hero.IsCollapsed);
        Assert.True(rig.ShowsHero);
        Assert.False(rig.ShowsBar);
        rig.Window.Close();
    });

    [Fact]
    public void Scrolled_TheBarTakesTheHerosPlace_AndTheListGetsTheRoom() => Headless.Run(() =>
    {
        var rig = Show();
        var viewport = rig.Scroll.Viewport.Height;

        rig.ScrollTo(800);

        Assert.True(rig.Hero.IsCollapsed);
        Assert.True(rig.ShowsBar);
        Assert.False(rig.ShowsHero);
        Assert.True(rig.Scroll.Viewport.Height > viewport + 100,
            $"viewport {rig.Scroll.Viewport.Height}, was {viewport}");
        rig.Window.Close();
    });

    [Fact]
    public void Collapsing_KeepsTheRowsWhereTheyWereOnScreen() => Headless.Run(() =>
    {
        var rig = Show();
        rig.Scroll.Offset = new Vector(0, 800);
        rig.Window.UpdateLayout();
        // Still expanded for this one layout: find a row in the lower half of the view.
        var row = Enumerable.Range(0, 120)
            .Select(i => (Index: i, Container: rig.List.ContainerFromIndex(i)))
            .Where(r => r.Container is not null)
            .Select(r => (r.Index, Y: r.Container!.TranslatePoint(default, rig.Window)!.Value.Y))
            .First(r => r.Y > 400);

        Settle(rig.Window);
        Assert.True(rig.Hero.IsCollapsed);

        var after = rig.List.ContainerFromIndex(row.Index)!.TranslatePoint(default, rig.Window)!.Value.Y;
        Assert.Equal(row.Y, after, 1.0);
        rig.Window.Close();
    });

    [Fact]
    public void BackAtTheTop_TheHeroReturns() => Headless.Run(() =>
    {
        var rig = Show();
        rig.ScrollTo(800);
        Assert.True(rig.Hero.IsCollapsed);

        rig.ScrollTo(300);
        Assert.True(rig.Hero.IsCollapsed);

        rig.ScrollTo(0);
        Assert.False(rig.Hero.IsCollapsed);
        Assert.True(rig.ShowsHero);
        Assert.False(rig.ShowsBar);
        rig.Window.Close();
    });

    [Fact]
    public void WhileAPointerIsPressedInTheList_NothingChangesUntilItIsReleased() => Headless.Run(() =>
    {
        // A drag that reorders rows scrolls the list at its edges; the rows
        // must not move by the hero's height under the pointer.
        var rig = Show();
        var at = rig.List.TranslatePoint(new Point(200, 60), rig.Window)!.Value;
        rig.Window.MouseDown(at, MouseButton.Left);

        rig.ScrollTo(800);
        Assert.False(rig.Hero.IsCollapsed);

        rig.Window.MouseUp(at, MouseButton.Left);
        Settle(rig.Window);
        Assert.True(rig.Hero.IsCollapsed);
        rig.Window.Close();
    });

    [Fact]
    public void AShortList_NeverCollapses() => Headless.Run(() =>
    {
        var rig = Show(count: 4);

        rig.ScrollTo(400);

        Assert.False(rig.Hero.IsCollapsed);
        Assert.True(rig.ShowsHero);
        rig.Window.Close();
    });

    [Fact]
    public void TheBarsPlay_IsTheHerosCommand_AndItsMoreMenuListsTheOtherActions() => Headless.Run(() =>
    {
        var rig = Show();
        rig.ScrollTo(800);

        Assert.Same(rig.HeroPlay.Command, rig.Hero.BarPlayButton.Command);
        Assert.Same(rig.Page.PlayAllCommand, rig.Hero.BarPlayButton.Command);
        Assert.Equal(rig.HeroPlay.IsEnabled, rig.Hero.BarPlayButton.IsEnabled);
        Assert.Equal(Strings.Get("common.play"), AutomationProperties.GetName(rig.Hero.BarPlayButton));
        Assert.Equal(Strings.Get("common.moreActions"), AutomationProperties.GetName(rig.Hero.BarMoreButton));

        var menu = rig.Hero.BuildMenu();
        var visible = rig.HeroRow.Items.OfType<Button>().Where(b => b.IsVisible && !ActionRow.GetKeep(b)).ToList();
        Assert.Equal(visible.Count, menu.Count);
        Assert.Equal(visible.Select(b => b.Command), menu.Select(m => m.Command));
        Assert.Equal(visible.Select(b => AutomationProperties.GetName(b)), menu.Select(m => m.Header as string));
        Assert.Equal(Strings.Get("common.shuffle"), menu[0].Header);
        Assert.Same(rig.Page.ShuffleCommand, menu[0].Command);
        rig.Window.Close();
    });

    [Fact]
    public void TheBarsTitle_IsThePages() => Headless.Run(() =>
    {
        var rig = Show();
        rig.ScrollTo(800);

        Assert.Contains(rig.Hero.Bar.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "Road trip" && t.IsEffectivelyVisible);
        rig.Window.Close();
    });

    [Fact]
    public void WithMotion_TheIncomingPartFades() => Headless.Run(() =>
    {
        var rig = Show();

        rig.ScrollTo(800);
        Assert.True(Fades(rig.Hero.Bar));

        rig.ScrollTo(0);
        Assert.True(Fades(rig.Hero.HeroPart));
        rig.Window.Close();
    });

    [Fact]
    public void UnderReducedMotion_TheChangeIsInstant() => Headless.Run(() =>
    {
        var rig = Show(reducedMotion: true);

        rig.ScrollTo(800);
        Assert.True(rig.Hero.IsCollapsed);
        Assert.False(Fades(rig.Hero.Bar));
        Assert.Equal(1, rig.Hero.Bar.Opacity);

        rig.ScrollTo(0);
        Assert.False(rig.Hero.IsCollapsed);
        Assert.False(Fades(rig.Hero.HeroPart));
        Assert.Equal(1, rig.Hero.HeroPart.Opacity);
        rig.Window.Close();
    });

    [Fact]
    public void FocusOnTheHerosPlay_MovesToTheBarsPlay_AndBack() => Headless.Run(() =>
    {
        var rig = Show();
        rig.HeroPlay.Focus(NavigationMethod.Tab);

        rig.ScrollTo(800);
        Assert.True(rig.Hero.IsCollapsed);
        Assert.Same(rig.Hero.BarPlayButton, rig.Focused);

        rig.ScrollTo(0);
        Assert.False(rig.Hero.IsCollapsed);
        Assert.Same(rig.HeroPlay, rig.Focused);
        rig.Window.Close();
    });

    [Fact]
    public void FocusOnAnotherHeroAction_MovesToTheBarsMore_AndBack() => Headless.Run(() =>
    {
        var rig = Show();
        rig.HeroShuffle.Focus(NavigationMethod.Tab);

        rig.ScrollTo(800);
        Assert.Same(rig.Hero.BarMoreButton, rig.Focused);

        rig.ScrollTo(0);
        Assert.True(rig.Focused is Control { IsEffectivelyVisible: true } focused &&
                    rig.Hero.Hero!.IsVisualAncestorOf(focused),
            $"focus on {rig.Focused}");
        rig.Window.Close();
    });

    [Fact]
    public void FocusNeverStaysOnAHiddenControl() => Headless.Run(() =>
    {
        var rig = Show();
        rig.HeroShuffle.Focus(NavigationMethod.Tab);
        rig.ScrollTo(800);
        Assert.True(rig.Hero.IsCollapsed);
        Assert.True(rig.Focused is Control { IsEffectivelyVisible: true });

        rig.ScrollTo(0);
        Assert.False(rig.Hero.IsCollapsed);
        Assert.True(rig.Focused is Control { IsEffectivelyVisible: true });
        rig.Window.Close();
    });

    [Fact]
    public void ShiftTabFromTheList_ReachesTheBar() => Headless.Run(() =>
    {
        var rig = Show();
        rig.ScrollTo(800);
        var row = Enumerable.Range(0, 120).Select(rig.List.ContainerFromIndex)
            .First(c => c is { IsEffectivelyVisible: true } && c.TranslatePoint(default, rig.Window)!.Value.Y > 100)!;
        rig.List.SelectedIndex = rig.List.IndexFromContainer(row);
        row.Focus(NavigationMethod.Tab);

        rig.Window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Assert.Same(rig.Hero.BarMoreButton, rig.Focused);
        rig.Window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Assert.Same(rig.Hero.BarPlayButton, rig.Focused);
        Assert.True(rig.Hero.IsCollapsed);
        rig.Window.Close();
    });

    [Fact]
    public void RestoredToAScrolledOffset_ShowsTheBarAtThatOffset_WithoutAFade() => Headless.Run(() =>
    {
        var rig = Show(saved: 900);

        Assert.True(rig.Hero.IsCollapsed);
        Assert.True(rig.ShowsBar);
        Assert.False(rig.ShowsHero);
        Assert.Equal(900, rig.Scroll.Offset.Y, 0.5);
        Assert.False(Fades(rig.Hero.Bar));
        rig.Window.Close();
    });

    [Fact]
    public void ANewPageInTheSameView_StartsExpanded() => Headless.Run(() =>
    {
        var rig = Show();
        rig.ScrollTo(800);
        Assert.True(rig.Hero.IsCollapsed);

        var other = new PlaylistPageViewModel(new FakeCatalog
        {
            Playlist = new PlaylistPage { PlaylistId = "PL2", Title = "Other", Tracks = Enumerable.Range(0, 120).Select(Track).ToList() },
        }, null!, null!, null!, "PL2");
        other.LoadAsync().GetAwaiter().GetResult();
        rig.View.DataContext = other;
        Settle(rig.Window);

        Assert.False(rig.Hero.IsCollapsed);
        Assert.True(rig.ShowsHero);
        rig.Window.Close();
    });

    [Fact]
    public void AFailedNextPage_BringsTheHeroBack_SinceItsTryAgainIsThere() => Headless.Run(() =>
    {
        var rig = Show();
        rig.ScrollTo(800);
        Assert.True(rig.Hero.IsCollapsed);

        rig.Page.LoadError = LoadError.From(new HttpRequestException("down"), LoadSubject.MoreTracks, rig.Page.LoadMoreCommand);
        Settle(rig.Window);

        Assert.False(rig.Hero.IsCollapsed);
        Assert.True(rig.ShowsHero);

        rig.Page.LoadError = null;
        rig.ScrollTo(rig.Scroll.Offset.Y + 10);
        Assert.True(rig.Hero.IsCollapsed);
        rig.Window.Close();
    });

    [Fact]
    public void AFailedFirstLoad_ShowsNeitherHeroNorBar() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var page = new PlaylistPageViewModel(new FakeCatalog(), null!, null!, null!, "PL1");
        page.LoadAsync().GetAwaiter().GetResult();
        Assert.NotNull(page.LoadError);
        var view = new Views.PlaylistPage { DataContext = page };
        var window = new Window { Width = 760, Height = 560, DataContext = new MainViewModel(), Content = view };
        window.Show();
        Settle(window);
        var hero = view.GetVisualDescendants().OfType<CollapsingHero>().Single();

        Assert.False(hero.Hero!.IsEffectivelyVisible);
        Assert.False(hero.Bar.IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public void ThePagesThatHaveAHero_AllUseTheControl() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        Control[] views =
        [
            new Views.PlaylistPage(), new Views.AlbumPage(), new Views.LocalPlaylistPage(), new Views.StationPage(),
        ];
        foreach (var view in views)
        {
            var window = new Window { Width = 760, Height = 560, DataContext = new MainViewModel(), Content = view };
            window.Show();
            Settle(window);
            var hero = view.GetLogicalDescendants().OfType<CollapsingHero>().Single();
            Assert.NotNull(hero.Hero);
            Assert.NotNull(hero.List);
            window.Close();
        }
    });
}
