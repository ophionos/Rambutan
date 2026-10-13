using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Rambutan.App.Controls;

namespace Rambutan.App.Tests;

public class ChipListTests
{
    /// <summary>Three chips with the second one on, as the Search and Library pages
    /// set them up; every item that gets selected afterwards is recorded.</summary>
    private static (Window Window, ChipList Chips, List<object> Selected) Show()
    {
        var chips = new ChipList
        {
            ItemsSource = new[] { "all", "songs", "albums" },
            SelectionMode = SelectionMode.AlwaysSelected,
        };
        var window = new Window { Width = 400, Height = 100, Content = chips };
        window.Show();
        chips.SelectedIndex = 1;
        window.UpdateLayout();

        var selected = new List<object>();
        chips.SelectionChanged += (_, e) => selected.AddRange(e.AddedItems.Cast<object>());
        return (window, chips, selected);
    }

    private static Point Centre(Window window, ChipList chips, int index)
    {
        var chip = chips.ContainerFromIndex(index)!;
        return chip.TranslatePoint(new Point(chip.Bounds.Width / 2, chip.Bounds.Height / 2), window)!.Value;
    }

    private static void Click(Window window, Point at, RawInputModifiers modifiers)
    {
        window.MouseDown(at, MouseButton.Left, modifiers);
        window.MouseUp(at, MouseButton.Left, modifiers);
    }

    [Fact]
    public void CtrlClickingTheSelectedChip_KeepsIt() => Headless.Run(() =>
    {
        var (window, chips, selected) = Show();

        Click(window, Centre(window, chips, 1), RawInputModifiers.Control);

        Assert.Equal("songs", chips.SelectedItem);
        Assert.Empty(selected);
    });

    [Fact]
    public void CtrlSpaceOnTheSelectedChip_KeepsIt() => Headless.Run(() =>
    {
        var (window, chips, selected) = Show();
        chips.ContainerFromIndex(1)!.Focus();

        window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, " ");

        Assert.Equal("songs", chips.SelectedItem);
        Assert.Empty(selected);
    });

    [Fact]
    public void ClickingAnotherChip_SelectsIt() => Headless.Run(() =>
    {
        var (window, chips, selected) = Show();

        Click(window, Centre(window, chips, 2), RawInputModifiers.None);

        Assert.Equal("albums", chips.SelectedItem);
        Assert.Equal(["albums"], selected);
    });

    [Fact]
    public void ArrowKeys_StillMoveTheSelection() => Headless.Run(() =>
    {
        var (window, chips, selected) = Show();
        chips.ContainerFromIndex(1)!.Focus();

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);

        Assert.Equal("albums", chips.SelectedItem);
        Assert.Equal(["albums"], selected);
    });
}
