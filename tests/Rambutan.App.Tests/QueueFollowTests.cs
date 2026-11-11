using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>The queue list keeps its current row at the top while the user
/// leaves it alone, and stays where the user scrolled it until the pane opens again.</summary>
public class QueueFollowTests
{
    private const double RowHeight = 40;

    private static TrackInfo Track(int i) =>
        new() { VideoId = "v" + i, Title = "Song " + i, Artists = [new("Artist")] };

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed record Setup(Window Window, Border Pane, ListBox List, QueueModel Queue, QueueViewModel Vm)
    {
        public ScrollViewer Viewer => List.GetVisualDescendants().OfType<ScrollViewer>().First();

        /// <summary>The current row's top, from the viewport's top.</summary>
        public double CurrentTop()
        {
            var item = Vm.Items.Single(i => i.IsCurrent);
            var row = List.ContainerFromItem(item)!;
            return row.TranslatePoint(default, Viewer)!.Value.Y;
        }
    }

    private static Setup Show(int count, int current)
    {
        var queue = new QueueModel();
        queue.SetQueue(Enumerable.Range(0, count).Select(Track), current);
        var vm = new QueueViewModel(queue, null);
        var list = new ListBox
        {
            ItemsSource = vm.Items,
            ItemTemplate = new FuncDataTemplate<QueueItemViewModel>((item, _) =>
                new TextBlock { Height = RowHeight, Text = item?.Title }),
        };
        var pane = new Border { Child = list };
        var window = new Window { Width = 300, Height = 400, Content = pane };
        // Before the window shows, as the main window attaches it.
        QueueFollow.Attach(list, pane, vm);
        Dispatcher.UIThread.RunJobs();
        window.Show();
        Settle(window);
        return new Setup(window, pane, list, queue, vm);
    }

    [Fact]
    public void AtStart_TheCurrentRowIsAtTheTop_WithThePlayedRowsAboveIt() => Headless.Run(() =>
    {
        var s = Show(count: 30, current: 10);

        Assert.True(s.Viewer.Offset.Y > 0);
        Assert.Equal(0, s.CurrentTop(), 2);
        s.Window.Close();
    });

    [Fact]
    public void ANewTrack_MovesToTheTop_WhileTheUserHasNotScrolled() => Headless.Run(() =>
    {
        var s = Show(count: 30, current: 10);

        s.Queue.NextTrack(TrackEndReason.Natural);
        s.Vm.Refresh();
        Settle(s.Window);

        Assert.Equal(11, s.Vm.Items.Single(i => i.IsCurrent).Index);
        Assert.Equal(0, s.CurrentTop(), 2);
        s.Window.Close();
    });

    [Fact]
    public void AfterAUserScroll_ANewTrackLeavesTheScrollAlone_UntilThePaneOpensAgain() => Headless.Run(() =>
    {
        var s = Show(count: 30, current: 10);
        var scrolled = s.Viewer.Offset.Y - 3 * RowHeight;
        s.Viewer.Offset = s.Viewer.Offset.WithY(scrolled);
        Settle(s.Window);

        s.Queue.NextTrack(TrackEndReason.Natural);
        s.Vm.Refresh();
        Settle(s.Window);

        Assert.Equal(scrolled, s.Viewer.Offset.Y, 0.5);

        s.Pane.IsVisible = false;
        Settle(s.Window);
        s.Pane.IsVisible = true;
        Settle(s.Window);

        Assert.Equal(0, s.CurrentTop(), 2);
        s.Window.Close();
    });

    [Fact]
    public void NearTheEndOfAShortQueue_TheListStopsAtItsEnd_AndStillFollows() => Headless.Run(() =>
    {
        var s = Show(count: 12, current: 9);
        var max = s.Viewer.Extent.Height - s.Viewer.Viewport.Height;

        Assert.Equal(max, s.Viewer.Offset.Y, 0.5);
        Assert.True(s.CurrentTop() > 0);

        s.Queue.NextTrack(TrackEndReason.Natural);
        s.Vm.Refresh();
        Settle(s.Window);
        Assert.Equal(max, s.Viewer.Offset.Y, 0.5);

        // Back to an earlier track: following was never lost to the clamp.
        s.Queue.JumpTo(2);
        s.Vm.Refresh();
        Settle(s.Window);
        Assert.Equal(0, s.CurrentTop(), 2);
        s.Window.Close();
    });
}
