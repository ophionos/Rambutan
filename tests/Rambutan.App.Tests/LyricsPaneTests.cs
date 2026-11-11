using System.Net.Http;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Services.Demo;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Layout;
using Rambutan.Core.Models;
using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Providers;

namespace Rambutan.App.Tests;

/// <summary>The lyrics pane as a control, the Now Playing layout around it, and the
/// demo's LRCLIB stand-in.</summary>
public class LyricsPaneTests
{
    private static readonly TrackInfo Crush = new()
    {
        VideoId = "crush",
        Title = "Instant Crush (feat. Julian Casablancas)",
        Artists = [new("Daft Punk", "UCdp"), new("Julian Casablancas")],
        Album = "Random Access Memories",
        Duration = TimeSpan.FromSeconds(338),
    };

    private static (LyricsViewModel Vm, List<TimeSpan> Seeks) Loaded(int lines = 40)
    {
        var seeks = new List<TimeSpan>();
        var doc = new LyricsDocument
        {
            Source = LrcLibProvider.SourceName,
            IsSynced = true,
            Lines = Enumerable.Range(0, lines)
                .Select(i => new LyricLine($"line {i}", TimeSpan.FromSeconds(4 * (i + 1))))
                .ToArray(),
        };
        var vm = new LyricsViewModel(
            (_, _) => Task.FromResult(new LyricsLookup(LyricsStatus.Found, doc)),
            p =>
            {
                seeks.Add(p);
                return Task.CompletedTask;
            });
        vm.OnTrackChanged(Crush);
        return (vm, seeks);
    }

    private static (Window Window, LyricsPane Pane) Show(LyricsViewModel vm, bool reducedMotion = true)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var pane = new LyricsPane { DataContext = vm };
        var window = new Window { Width = 600, Height = 500, Content = pane };
        if (reducedMotion)
            window.Classes.Add("reducedmotion");
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, pane);
    }

    [Theory]
    [InlineData(0, 30, 400, 2000, 0)]        // near the top: can't scroll above zero
    [InlineData(800, 40, 400, 2000, 660)]    // centre (820) at 40 % (160) of the viewport
    [InlineData(1950, 40, 400, 2000, 1600)]  // near the end: clamped to the extent
    [InlineData(100, 40, 400, 300, 0)]       // content shorter than the viewport
    public void TargetOffset_PutsTheLineCentreFortyPercentDown(
        double top, double height, double viewport, double extent, double expected) =>
        Assert.Equal(expected, LyricsPane.TargetOffset(top, height, viewport, extent), precision: 6);

    [Fact]
    public void FollowingPlayback_KeepsTheActiveLineFortyPercentDown() => Headless.Run(() =>
    {
        var (vm, _) = Loaded();
        var (window, pane) = Show(vm);
        var scroller = pane.FindControl<ScrollViewer>("Scroller")!;
        var list = pane.FindControl<ItemsControl>("LinesList")!;

        vm.OnPosition(TimeSpan.FromSeconds(4 * 21 + 1)); // line 20
        window.UpdateLayout();

        var line = (Control)list.ContainerFromIndex(20)!;
        var top = line.TranslatePoint(default, (Visual)scroller.Content!)!.Value.Y;
        var centreInViewport = top + line.Bounds.Height / 2 - scroller.Offset.Y;
        Assert.Equal(scroller.Viewport.Height * LyricsPane.AnchorFraction, centreInViewport, precision: 1);
        window.Close();
    });

    [Fact]
    public void ScrollingByHand_StopsFollowing_UntilResumed() => Headless.Run(() =>
    {
        var (vm, _) = Loaded();
        var (window, pane) = Show(vm);
        var scroller = pane.FindControl<ScrollViewer>("Scroller")!;
        var resume = pane.FindControl<Button>("ResumeButton")!;
        Assert.True(pane.IsFollowing);
        Assert.False(resume.IsVisible);

        window.MouseWheel(new Point(300, 250), new Vector(0, -3));
        Assert.False(pane.IsFollowing);
        Assert.True(resume.IsVisible);
        Assert.Equal("Back to the current line", AutomationProperties.GetName(resume));

        // Playback moves on, but the listener's scroll position stays theirs.
        var held = scroller.Offset.Y;
        vm.OnPosition(TimeSpan.FromSeconds(4 * 30 + 1));
        Assert.Equal(held, scroller.Offset.Y);

        resume.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(pane.IsFollowing);
        Assert.False(resume.IsVisible);
        Assert.NotEqual(held, scroller.Offset.Y);
        window.Close();
    });

    [Fact]
    public void TheSwitchAndTheFooter_AreStripsNoLineIsDrawnOver() => Headless.Run(() =>
    {
        var doc = new LyricsDocument
        {
            Source = LrcLibProvider.SourceName,
            IsSynced = true,
            Lines = Enumerable.Range(0, 40)
                .Select(i => new LyricLine($"line {i}", TimeSpan.FromSeconds(4 * (i + 1))))
                .ToArray(),
        };
        var vm = new LyricsViewModel(
            (_, _) => Task.FromResult(new LyricsLookup(LyricsStatus.Found, doc)),
            _ => Task.CompletedTask,
            applySource: _ => { });
        vm.OnTrackChanged(Crush);
        var (window, pane) = Show(vm);
        var switcher = pane.FindControl<ListBox>("SourceSwitch")!;
        var scroller = pane.FindControl<ScrollViewer>("Scroller")!;
        var footer = pane.FindControl<StackPanel>("Footer")!;
        var first = (Control)pane.FindControl<ItemsControl>("LinesList")!.ContainerFromIndex(0)!;
        Assert.True(switcher.IsVisible);

        Rect In(Control c) => new(c.TranslatePoint(default, pane)!.Value, c.Bounds.Size);
        Assert.False(In(switcher).Intersects(In(first)), "the first line runs under the source switch");
        Assert.False(In(footer).Intersects(In(scroller)), "the footer runs under the scroller");
        Assert.False(In(switcher).Intersects(In(scroller)), "the source switch runs over the scroller");
        window.Close();
    });

    [Fact]
    public void ResumeAfter_IsAboutThreeSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(3), LyricsPane.ResumeAfter);

    [Fact]
    public void ClickingALine_Seeks_AndEveryLineIsNamed() => Headless.Run(() =>
    {
        var (vm, seeks) = Loaded(lines: 6);
        var (window, pane) = Show(vm);
        var buttons = pane.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("lyricline")).ToArray();
        Assert.Equal(6, buttons.Length);
        Assert.All(buttons, b => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(b))));
        Assert.Equal("line 3", AutomationProperties.GetName(buttons[3]));

        buttons[3].Command!.Execute(buttons[3].CommandParameter);
        Assert.Equal([TimeSpan.FromSeconds(16)], seeks);
        Assert.Contains("active", buttons[3].Classes);

        // One Tab stop for the lines, arrows inside.
        Assert.True(RovingFocus.GetIsEnabled(pane.FindControl<ItemsControl>("LinesList")!));
        window.Close();
    });

    [Theory]
    [InlineData(LyricsStatus.NotFound, "No lyrics found")]
    [InlineData(LyricsStatus.Instrumental, "Instrumental")]
    [InlineData(LyricsStatus.Unavailable, "Couldn't reach lyrics")]
    public void EachEmptyState_SaysWhatHappened(LyricsStatus status, string title) => Headless.Run(() =>
    {
        var vm = new LyricsViewModel((_, _) => Task.FromResult(new LyricsLookup(status, LyricsDocument.Empty("lrclib"))),
            _ => Task.CompletedTask);
        vm.OnTrackChanged(Crush);
        var (window, pane) = Show(vm);

        var shown = pane.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();
        Assert.Contains(title, shown);
        if (status is LyricsStatus.Unavailable)
            Assert.Contains("Try again", shown);
        window.Close();
    });

    [Fact]
    public void Loading_ShowsLineShapedBones() => Headless.Run(() =>
    {
        var vm = new LyricsViewModel((_, _) => new TaskCompletionSource<LyricsLookup>().Task, _ => Task.CompletedTask);
        vm.OnTrackChanged(Crush);
        var (window, pane) = Show(vm);

        Assert.Equal(LyricsState.Loading, vm.State);
        Assert.Contains(pane.GetVisualDescendants().OfType<Border>(),
            b => b.Classes.Contains("skeleton") && b.IsEffectivelyVisible);
        window.Close();
    });

    private static (Window Window, NowPlayingView View, NowPlayingViewModel Vm) NowPlaying(
        double width, bool artwork = false, double height = 700, bool showLyrics = true,
        LyricsViewModel? lyricsVm = null, Services.AppearanceSettings? appearance = null)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var lyrics = lyricsVm ?? Loaded(lines: 8).Vm;
        var player = new PlayerBarViewModel
        {
            NowPlaying = Crush, ShownTrack = Crush, DisplayedTrack = Crush,
            ArtworkUrl = "https://example.com/crush.jpg",
        };
        var vm = new NowPlayingViewModel(player, lyrics,
            new Services.AppSettings { ShowLyrics = showLyrics, ShowArtwork = artwork }, appearance);
        var view = new NowPlayingView { DataContext = vm };
        var window = new Window
        {
            Width = width, Height = height, DataContext = new MainViewModel(), Content = view,
        };
        window.Classes.Add("reducedmotion");
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    private static void Relayout(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [Fact]
    public void LyricsAlone_ACentredColumn_WithItsScrollbarAtThePanesEdge() => Headless.Run(() =>
    {
        var (window, view, vm) = NowPlaying(width: 1400);
        Assert.Equal(NowPlayingView.StageLayout.LyricsOnly, view.CurrentLayout);

        // The lines are capped and centred on the stage, under the title; the
        // scroller reaches past the stage into the margin, near the pane's edge.
        var pane = view.GetVisualDescendants().OfType<LyricsPane>().Single();
        var stage = view.FindControl<Grid>("Stage")!;
        var identity = view.FindControl<Grid>("Identity")!;
        var column = pane.FindControl<Panel>("Column")!;
        var scroller = pane.FindControl<ScrollViewer>("Scroller")!;
        Assert.Equal(LyricsPane.ColumnMaxWidth, column.Bounds.Width, 0.5);
        Assert.Equal(view.Bounds.Width / 2, column.TranslatePoint(new(column.Bounds.Width / 2, 0), view)!.Value.X, 1);
        var scrollerRight = scroller.TranslatePoint(new(scroller.Bounds.Width, 0), view)!.Value.X;
        Assert.Equal(stage.TranslatePoint(new(stage.Bounds.Width, 0), view)!.Value.X + NowPlayingView.LyricsReach,
            scrollerRight, 1);
        Assert.True(pane.Bounds.Top >= identity.Bounds.Bottom);
        Assert.False(view.FindControl<Panel>("ArtSlot")!.IsVisible);

        // Toggling off (the crash this guards: a stale row index) and back on.
        vm.ToggleLyricsCommand.Execute(null);
        Relayout(window);
        Assert.Equal(NowPlayingView.StageLayout.ArtOnly, view.CurrentLayout);
        Assert.False(pane.IsEffectivelyVisible);
        vm.ToggleLyricsCommand.Execute(null);
        Relayout(window);
        Assert.Equal(NowPlayingView.StageLayout.LyricsOnly, view.CurrentLayout);

        // Narrow, the column takes the whole width the lyrics have.
        window.Width = 600;
        Relayout(window);
        Assert.Equal(NowPlayingView.StageLayout.LyricsOnly, view.CurrentLayout);
        Assert.Equal(stage.Bounds.Width + 2 * NowPlayingView.LyricsReach, column.Bounds.Width, 0.5);
        window.Close();
    });

    [Fact]
    public void ArtworkAndLyrics_SitSideBySide_WhenWide() => Headless.Run(() =>
    {
        var (window, view, vm) = NowPlaying(width: 1400, artwork: true);
        Assert.Equal(NowPlayingView.StageLayout.SideBySide, view.CurrentLayout);

        var pane = view.GetVisualDescendants().OfType<LyricsPane>().Single();
        var art = view.FindControl<Border>("Art")!;
        var artRight = art.TranslatePoint(new(art.Bounds.Width, 0), view)!.Value.X;
        var paneLeft = pane.TranslatePoint(default, view)!.Value.X;
        Assert.True(art.Bounds.Height > 0);
        Assert.True(artRight <= paneLeft, $"artwork ends at {artRight}, lyrics start at {paneLeft}");
        Assert.True(artRight < view.Bounds.Width / 2);
        Assert.False(vm.IsArtworkCompact);
        window.Close();
    });

    /// <summary>Resizes the window so the stage is <paramref name="width"/> by
    /// <paramref name="height"/>, whatever the margins and notes around it.</summary>
    private static Grid SizeStage(Window window, NowPlayingView view, double width, double height)
    {
        var stage = view.FindControl<Grid>("Stage")!;
        for (var i = 0; i < 3; i++)
        {
            window.Width += width - stage.Bounds.Width;
            window.Height += height - stage.Bounds.Height;
            Relayout(window);
        }
        Assert.Equal(width, stage.Bounds.Width, 0.5);
        Assert.Equal(height, stage.Bounds.Height, 0.5);
        return stage;
    }

    private static Rect BoundsIn(Control control, Visual relativeTo) =>
        new(control.TranslatePoint(default, relativeTo)!.Value, control.Bounds.Size);

    [Fact]
    public void Stacked_TheLyricsTakeThePane_UnderAHeaderOfCoverTitleAndArtist() => Headless.Run(() =>
    {
        var (window, view, vm) = NowPlaying(width: 700, artwork: true);
        Assert.Equal(NowPlayingView.StageLayout.Stacked, view.CurrentLayout);
        Assert.True(vm.IsArtworkCompact);

        var stage = view.FindControl<Grid>("Stage")!;
        var identity = view.FindControl<Grid>("Identity")!;
        var pane = view.FindControl<LyricsPane>("LyricsHost")!;
        var art = view.FindControl<Border>("Art")!;
        var title = view.FindControl<Button>("TitleLink")!;
        var credits = view.FindControl<WrapPanel>("Credits")!;
        var scroller = pane.FindControl<ScrollViewer>("Scroller")!;

        // The header in the first row, the lyrics under it to the stage's foot,
        // with no limit on their lines.
        Assert.Equal(0, Grid.GetRow(identity));
        Assert.Equal(1, Grid.GetRow(pane));
        Assert.True(pane.Bounds.Top >= identity.Bounds.Bottom);
        Assert.Equal(stage.Bounds.Height, pane.Bounds.Bottom, 0.5);
        Assert.True(scroller.Bounds.Height > stage.Bounds.Height / 2,
            $"lines get {scroller.Bounds.Height} of {stage.Bounds.Height}");

        // The small cover on the left, the title over the artist to its right,
        // the two centred on the cover.
        Assert.Equal(NowPlayingLayout.HeaderArt, art.Bounds.Width, 0.5);
        Assert.Equal(NowPlayingLayout.HeaderArt, art.Bounds.Height, 0.5);
        var cover = BoundsIn(art, stage);
        var titleBox = BoundsIn(title, stage);
        var creditsBox = BoundsIn(credits, stage);
        Assert.True(titleBox.Left >= cover.Right && creditsBox.Left >= cover.Right,
            $"cover ends at {cover.Right}, title starts at {titleBox.Left}, artist at {creditsBox.Left}");
        Assert.Equal(titleBox.Left, creditsBox.Left, 0.5);
        Assert.True(titleBox.Bottom <= creditsBox.Top + 0.5);
        var textMiddle = (titleBox.Top + creditsBox.Bottom) / 2;
        Assert.True(Math.Abs(cover.Center.Y - textMiddle) <= 2,
            $"cover centre {cover.Center.Y}, text {titleBox.Top}..{creditsBox.Bottom}");
        Assert.Equal(NowPlayingLayout.CompactTitleSize, title.FontSize);
        Assert.Equal(1, view.FindControl<TextBlock>("TitleText")!.MaxLines);

        // Wider, they go side by side and the cover draws full size again.
        window.Width = 1400;
        Relayout(window);
        Assert.Equal(NowPlayingView.StageLayout.SideBySide, view.CurrentLayout);
        Assert.False(vm.IsArtworkCompact);
        Assert.True(art.Bounds.Width > NowPlayingLayout.HeaderArt);
        window.Close();
    });

    private static LyricsViewModel InState(LyricsStatus status)
    {
        var custom = new CustomLyricsAccess(_ => null, (_, _) => { }, _ => { },
            (_, _) => Task.FromResult(LyricsLookup.NotFound));
        var vm = new LyricsViewModel(
            (_, _) => Task.FromResult(status is LyricsStatus.NotLookedUp
                ? LyricsLookup.NotLookedUp
                : new LyricsLookup(status, LyricsDocument.Empty("lrclib"))),
            _ => Task.CompletedTask, custom: custom);
        vm.OnTrackChanged(Crush);
        return vm;
    }

    private static string? Label(Button button) => button.Content switch
    {
        string text => text,
        _ => button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text,
    };

    [Theory]
    [InlineData(LyricsStatus.NotLookedUp, new[] { "Look up lyrics online", "Add your own lyrics" })]
    [InlineData(LyricsStatus.NotFound, new[] { "Add your own lyrics" })]
    [InlineData(LyricsStatus.Unavailable, new[] { "Try again" })]
    public void AtTheSmallestStage_AStatesButtons_ScrollIntoReach(LyricsStatus status, string[] labels) =>
        Headless.Run(() =>
        {
            var (window, view, _) = NowPlaying(width: 700, artwork: true, lyricsVm: InState(status));
            SizeStage(window, view, 560, 280);
            Assert.Equal(NowPlayingView.StageLayout.Stacked, view.CurrentLayout);

            var pane = view.FindControl<LyricsPane>("LyricsHost")!;
            var states = pane.FindControl<ScrollViewer>("StateScroller")!;
            Assert.True(states.IsEffectivelyVisible);
            var viewport = BoundsIn(states, pane);
            Assert.True(viewport.Top >= -0.5 && viewport.Bottom <= pane.Bounds.Height + 0.5,
                $"the states' scroller spans {viewport.Top}..{viewport.Bottom} of a pane {pane.Bounds.Height} high");

            foreach (var label in labels)
            {
                var button = states.GetVisualDescendants().OfType<Button>()
                    .Single(b => b.IsEffectivelyVisible && Label(b) == label);
                Assert.Same(states, button.FindAncestorOfType<ScrollViewer>());

                // Focus brings it into the viewport, as Tab does.
                button.Focus(NavigationMethod.Tab);
                Relayout(window);
                var at = BoundsIn(button, states);
                Assert.True(at.Top >= -0.5 && at.Bottom <= states.Viewport.Height + 0.5,
                    $"'{label}' sits at {at.Top}..{at.Bottom} of a viewport {states.Viewport.Height} high");
            }
            window.Close();
        });

    [Fact]
    public void ArtOnly_AtTheDefaultSize_TheArtworkGrows_AndTheArtistStaysInTheStage() => Headless.Run(() =>
    {
        var (window, view, _) = NowPlaying(width: 1188, artwork: true, showLyrics: false);
        var stage = SizeStage(window, view, 1092, 590);
        Assert.Equal(NowPlayingView.StageLayout.ArtOnly, view.CurrentLayout);

        var art = view.FindControl<Border>("Art")!;
        var title = view.FindControl<Button>("TitleLink")!;
        var credits = view.FindControl<WrapPanel>("Credits")!;
        Assert.True(art.Bounds.Width > 300, $"artwork is {art.Bounds.Width}");
        Assert.True(title.FontSize > NowPlayingLayout.MinTitleSize, $"title is {title.FontSize}");
        // The links laid out with their text, so the line measured is a real one.
        var links = credits.Children.OfType<Button>().ToArray();
        Assert.All(links, link => Assert.True(link.Bounds.Width > 0, $"a link is {link.Bounds}"));
        var bottom = BoundsIn(credits, stage).Bottom;
        Assert.True(bottom <= stage.Bounds.Height + 0.5, $"artist line ends at {bottom} of {stage.Bounds.Height}");
        window.Close();
    });

    [Fact]
    public void ArtOnly_WithTheRing_AtTheDefaultAndAShortStage_TheRingAndTheArtistStayInTheStage() => Headless.Run(() =>
    {
        var appearance = new Services.AppearanceSettings
        {
            VisualizerStyle = Services.VisualizerStyle.Radial,
            Motion = Services.MotionPreference.Full,
        };
        // A shorter stage puts the artwork at its floor, and with the ring's
        // reach that no longer fits (as before this layout).
        foreach (var (width, height) in new[] { (1092.0, 590.0), (560.0, 400.0) })
        {
            var (window, view, vm) = NowPlaying(width: 1188, artwork: true, showLyrics: false, appearance: appearance);
            var stage = SizeStage(window, view, width, height);
            Assert.True(vm.ShowsRing);

            var artSlot = view.FindControl<Panel>("ArtSlot")!;
            var slot = BoundsIn(artSlot, stage);
            var credits = BoundsIn(view.FindControl<WrapPanel>("Credits")!, stage);
            // The slot keeps the ring's reach clear on every side.
            Assert.Equal(RadialRenderer.Margin, artSlot.Margin.Left, 0.5);
            Assert.Equal(RadialRenderer.Margin, artSlot.Margin.Top, 0.5);
            var ringTop = slot.Top - artSlot.Margin.Top;
            Assert.True(ringTop >= -0.5, $"{width}x{height}: ring starts at {ringTop}");
            Assert.True(credits.Bottom <= stage.Bounds.Height + 0.5,
                $"{width}x{height}: artist line ends at {credits.Bottom} of {stage.Bounds.Height}");
            window.Close();
        }
    });

    [Fact]
    public void EachLayout_AfterTheOthers_IsAsAFreshViewWouldHaveIt() => Headless.Run(() =>
    {
        // Stacked, side by side, the artwork alone, the lyrics alone, stacked.
        var steps = new (double Width, bool Artwork, bool Lyrics, NowPlayingView.StageLayout Layout)[]
        {
            (700, true, true, NowPlayingView.StageLayout.Stacked),
            (1400, true, true, NowPlayingView.StageLayout.SideBySide),
            (1400, true, false, NowPlayingView.StageLayout.ArtOnly),
            (1400, false, true, NowPlayingView.StageLayout.LyricsOnly),
            (700, true, true, NowPlayingView.StageLayout.Stacked),
        };

        var (window, view, vm) = NowPlaying(width: steps[0].Width, artwork: steps[0].Artwork);
        foreach (var step in steps)
        {
            window.Width = step.Width;
            vm.ShowArtwork = step.Artwork;
            vm.ShowLyrics = step.Lyrics;
            Relayout(window);
            Assert.Equal(step.Layout, view.CurrentLayout);

            var (freshWindow, fresh, _) = NowPlaying(width: step.Width, artwork: step.Artwork, showLyrics: step.Lyrics);
            Assert.Equal(step.Layout, fresh.CurrentLayout);
            foreach (var name in new[] { "Art", "TitleLink", "Credits", "LyricsHost" })
            {
                var moved = view.FindControl<Control>(name)!;
                var made = fresh.FindControl<Control>(name)!;
                Assert.Equal(made.IsEffectivelyVisible, moved.IsEffectivelyVisible);
                if (!made.IsEffectivelyVisible)
                    continue;
                var a = BoundsIn(moved, view);
                var b = BoundsIn(made, fresh);
                Assert.True(Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5
                    && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5,
                    $"{step.Layout}: {name} is {a}, fresh {b}");
            }
            Assert.Equal(fresh.FindControl<Button>("TitleLink")!.FontSize, view.FindControl<Button>("TitleLink")!.FontSize);
            freshWindow.Close();
        }
        window.Close();
    });

    [Fact]
    public void TheLyricsToggle_IsNamedForWhatItDoes_AndNothingIsUnnamed() => Headless.Run(() =>
    {
        var (window, view, vm) = NowPlaying(width: 1100);
        var toggle = view.FindControl<Button>("LyricsToggle")!;
        Assert.Equal("Show artwork", AutomationProperties.GetName(view.FindControl<Button>("ArtworkToggle")!));
        Assert.Equal("Hide lyrics", AutomationProperties.GetName(toggle));
        Assert.Contains("active", toggle.Classes);

        vm.ToggleLyricsCommand.Execute(null);
        Assert.Equal("Show lyrics", AutomationProperties.GetName(toggle));

        vm.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var unnamed = view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.TemplatedParent is null)
            .Where(b => string.IsNullOrEmpty(AutomationProperties.GetName(b)) && b.Content is not (string or TextBlock))
            .ToArray();
        Assert.Empty(unnamed);
        window.Close();
    });

    private static readonly DemoOptions Demo = new(true, ".", TimeSpan.Zero, new HashSet<string>());

    private static LyricsProviderChain DemoChain(DemoOptions options) =>
        new([new LrcLibProvider(new HttpClient(new DemoLyricsHandler(options)))]);

    [Theory]
    [InlineData("Instant Crush (feat. Julian Casablancas)", LyricsStatus.Found, true)]
    [InlineData("The Game of Love", LyricsStatus.Found, true)]
    [InlineData("Something Else Entirely", LyricsStatus.Found, true)]
    [InlineData("Giorgio by Moroder", LyricsStatus.Found, false)]
    [InlineData("Motherboard", LyricsStatus.Instrumental, false)]
    [InlineData("Touch (feat. Paul Williams)", LyricsStatus.NotFound, false)]
    public async Task TheDemo_HasEveryPaneState(string title, LyricsStatus status, bool synced)
    {
        var track = Crush with { Title = title };
        var result = await DemoChain(Demo).LookupAsync(LyricsQuery.FromTrack(track), CancellationToken.None);

        Assert.Equal(status, result.Status);
        if (status is LyricsStatus.Found)
        {
            Assert.Equal(synced, result.Document!.IsSynced);
            Assert.NotEmpty(result.Document.Lines);
            if (synced) // stamped within the track
                Assert.True(result.Document.Lines[^1].Start < track.Duration);
        }
    }

    [Fact]
    public async Task TheDemo_HasCjkAndEmojiLines()
    {
        var result = await DemoChain(Demo).LookupAsync(LyricsQuery.FromTrack(Crush), CancellationToken.None);
        var text = string.Concat(result.Document!.Lines.Select(l => l.Text));
        Assert.Contains("夜", text);  // Japanese
        Assert.Contains("기", text);  // Korean
        Assert.Contains("还", text);  // Chinese
        Assert.Contains("📻", text);
    }

    [Fact]
    public async Task TheDemo_FailsLyricsOnRequest_AsUnavailable()
    {
        var failing = Demo with { FailingAreas = new HashSet<string> { "lyrics" } };
        var result = await DemoChain(failing).LookupAsync(LyricsQuery.FromTrack(Crush), CancellationToken.None);
        Assert.Equal(LyricsStatus.Unavailable, result.Status);
    }
}
