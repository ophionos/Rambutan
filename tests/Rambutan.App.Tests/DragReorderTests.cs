using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Tests;

public class DragReorderTests
{
    private const double RowHeight = 40;

    /// <summary>Five 40px rows of "title + a row button", like the queue.</summary>
    private static (Window Window, ListBox List, List<ReorderRequest> Requests) Show()
    {
        var requests = new List<ReorderRequest>();
        var list = new ListBox
        {
            ItemsSource = new[] { "a", "b", "c", "d", "e" },
            ItemTemplate = new FuncDataTemplate<string>((_, _) => new DockPanel
            {
                Height = RowHeight,
                Background = Avalonia.Media.Brushes.Transparent,
                Children =
                {
                    new Button { Content = "×", [DockPanel.DockProperty] = Dock.Right, Width = 40 },
                    new TextBlock { [!TextBlock.TextProperty] = new Avalonia.Data.Binding() },
                },
            }),
        };
        DragReorder.SetCommand(list, new RelayCommand<ReorderRequest>(r => requests.Add(r!)));

        var window = new Window { Width = 400, Height = 400, Content = list };
        window.Show();
        window.UpdateLayout();
        return (window, list, requests);
    }

    /// <summary>A point on row <paramref name="index"/>'s text, at
    /// <paramref name="fraction"/> of its height.</summary>
    private static Point Row(Window window, ListBox list, int index, double fraction = 0.5)
    {
        var row = list.ContainerFromIndex(index)!;
        return row.TranslatePoint(new Point(20, row.Bounds.Height * fraction), window)!.Value;
    }

    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
        // Several steps, like a real hand: the first crosses the threshold.
        var steps = 4;
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            window.MouseMove(new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t),
                RawInputModifiers.LeftMouseButton);
        }
        window.MouseUp(to, MouseButton.Left, RawInputModifiers.None);
    }

    [Theory]
    [InlineData(0, 3, 0.75, 3)] // down: into the gap below d, so after d
    [InlineData(0, 3, 0.25, 2)] // down: into the gap above d, so before it
    [InlineData(4, 1, 0.25, 1)] // up: above b
    [InlineData(4, 0, 0.10, 0)] // up: to the top
    public void DraggingARow_AsksToMoveIt(int from, int overRow, double fraction, int to) => Headless.Run(() =>
    {
        var (window, list, requests) = Show();

        Drag(window, Row(window, list, from), Row(window, list, overRow, fraction));

        Assert.Equal([new ReorderRequest(from, to)], requests);
    });

    [Fact]
    public void DroppingWhereItStarted_AsksNothing() => Headless.Run(() =>
    {
        var (window, list, requests) = Show();

        // Down into its own lower half, then back: still the same slot.
        Drag(window, Row(window, list, 2, 0.2), Row(window, list, 2, 0.9));

        Assert.Empty(requests);
    });

    [Fact]
    public void AClick_IsNotADrag() => Headless.Run(() =>
    {
        var (window, list, requests) = Show();
        var at = Row(window, list, 1);

        window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(at + new Point(0, DragReorder.Threshold - 2), RawInputModifiers.LeftMouseButton);
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);

        Assert.Empty(requests);
        Assert.Equal(1, list.SelectedIndex);
    });

    [Fact]
    public void PressingARowButton_NeverStartsADrag() => Headless.Run(() =>
    {
        var (window, list, requests) = Show();
        var button = list.ContainerFromIndex(0)!.GetVisualDescendants().OfType<Button>().First();
        var from = button.TranslatePoint(new Point(20, 20), window)!.Value;

        Drag(window, from, from + new Point(0, RowHeight * 3));

        Assert.Empty(requests);
    });

    [Theory]
    [InlineData(Key.Down, 3)]
    [InlineData(Key.Up, 1)]
    public void AltArrow_MovesTheSelectedRow(Key key, int to) => Headless.Run(() =>
    {
        var (window, list, requests) = Show();
        list.SelectedIndex = 2;
        list.ContainerFromIndex(2)!.Focus();

        window.KeyPress(key, RawInputModifiers.Alt, key == Key.Up ? PhysicalKey.ArrowUp : PhysicalKey.ArrowDown, null);

        Assert.Equal([new ReorderRequest(2, to)], requests);
    });

    [Fact]
    public void AltArrow_AtTheEdge_DoesNothing() => Headless.Run(() =>
    {
        var (window, list, requests) = Show();
        list.SelectedIndex = 0;
        list.ContainerFromIndex(0)!.Focus();

        window.KeyPress(Key.Up, RawInputModifiers.Alt, PhysicalKey.ArrowUp, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(requests);
        Assert.Equal(0, list.SelectedIndex);
    });

    [Theory]
    [InlineData(2, 0, 0)]
    [InlineData(2, 2, 2)]
    [InlineData(2, 3, 2)] // the gap right below itself: no move
    [InlineData(2, 5, 4)] // below the last row
    public void TargetIndex_CountsPositionsAfterTheRowIsTakenOut(int from, int slot, int expected) =>
        Assert.Equal(expected, DragReorder.TargetIndex(from, slot));
}
