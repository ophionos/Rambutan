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

    private static (Window Window, NowPlayingView View, NowPlayingViewModel Vm) NowPlaying(double width)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var (lyrics, _) = Loaded(lines: 8);
        var player = new PlayerBarViewModel { NowPlaying = Crush, ShownTrack = Crush };
        var vm = new NowPlayingViewModel(player, lyrics, new Services.AppSettings { ShowLyrics = true });
        var view = new NowPlayingView { DataContext = vm };
        var window = new Window
        {
            Width = width, Height = 700, DataContext = new MainViewModel(), Content = view,
        };
        window.Classes.Add("reducedmotion");
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    [Fact]
    public void LyricsOn_Overlay_ACentredColumn_Off_ArtOnly() => Headless.Run(() =>
    {
        var (window, view, vm) = NowPlaying(width: 1400);
        Assert.Equal(NowPlayingView.StageLayout.Overlay, view.CurrentLayout);

        // The lyrics column is capped and centred on the stage, under the title.
        var pane = view.GetVisualDescendants().OfType<LyricsPane>().Single();
        var stage = view.FindControl<Grid>("Stage")!;
        var identity = view.FindControl<StackPanel>("Identity")!;
        Assert.Equal(NowPlayingView.LyricsMaxWidth, pane.Bounds.Width, 0.5);
        Assert.Equal(stage.Bounds.Width / 2, pane.Bounds.Center.X, 1);
        Assert.True(pane.Bounds.Top >= identity.Bounds.Bottom);
        Assert.False(view.FindControl<Border>("Art")!.Bounds.Height > 0);

        // Toggling off (the crash this guards: a stale row index) and back on.
        vm.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();
        Assert.Equal(NowPlayingView.StageLayout.ArtOnly, view.CurrentLayout);
        Assert.False(pane.IsEffectivelyVisible);
        vm.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();
        Assert.Equal(NowPlayingView.StageLayout.Overlay, view.CurrentLayout);

        // Narrow, the column takes the whole stage.
        window.Width = 600;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(NowPlayingView.StageLayout.Overlay, view.CurrentLayout);
        Assert.Equal(stage.Bounds.Width, pane.Bounds.Width, 0.5);
        window.Close();
    });

    [Fact]
    public void TheLyricsToggle_IsNamedForWhatItDoes_AndNothingIsUnnamed() => Headless.Run(() =>
    {
        var (window, view, vm) = NowPlaying(width: 1100);
        var toggle = view.FindControl<Button>("LyricsToggle")!;
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
