using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Controls;

namespace Rambutan.App.Tests;

public class ItemActivationTests
{
    /// <summary>Rows of "title + a row button", like the track lists.</summary>
    private static (Window Window, ListBox List, List<string> Activated) Show()
    {
        var activated = new List<string>();
        var list = new ListBox
        {
            ItemsSource = new[] { "a", "b", "c" },
            ItemTemplate = new FuncDataTemplate<string>((item, _) => new DockPanel
            {
                Height = 40,
                Background = Avalonia.Media.Brushes.Transparent,
                Children =
                {
                    new Button { Content = "+", [DockPanel.DockProperty] = Dock.Right, Width = 40 },
                    new TextBlock { [!TextBlock.TextProperty] = new Avalonia.Data.Binding() },
                },
            }),
        };
        ItemActivation.SetCommand(list, new RelayCommand<string>(item => activated.Add(item!)));

        var window = new Window { Width = 400, Height = 300, Content = list };
        window.Show();
        window.UpdateLayout();

        // Headless input is stamped with real time, and a double-click only counts
        // if its second press lands within the platform's double-click interval.
        // On a cold CI runner the first press through a ListBox can take longer
        // than that to JIT, so the pair reads as two single clicks. One throwaway
        // double-click pays that cost here, then gets forgotten.
        DoubleClick(window, RowText(window, list, 0));
        activated.Clear();

        return (window, list, activated);
    }

    /// <summary>Centre of the row's text (first child that is not the button).</summary>
    private static Point RowText(Window window, ListBox list, int index)
    {
        var row = list.ContainerFromIndex(index)!;
        var text = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text is "a" or "b" or "c");
        return text.TranslatePoint(new Point(5, text.Bounds.Height / 2), window)!.Value;
    }

    private static Point RowButton(Window window, ListBox list, int index)
    {
        var button = list.ContainerFromIndex(index)!.GetVisualDescendants().OfType<Button>().First();
        return button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
    }

    private static void DoubleClick(Window window, Point at)
    {
        for (var i = 0; i < 2; i++)
        {
            window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
        }
    }

    [Fact]
    public void DoubleClickingARow_ActivatesThatRowsItem() => Headless.Run(() =>
    {
        var (window, list, activated) = Show();

        DoubleClick(window, RowText(window, list, 1));

        Assert.Equal(["b"], activated);
    });

    [Fact]
    public void DoubleClickingARowButton_DoesNotAlsoActivateTheRow() => Headless.Run(() =>
    {
        var (window, list, activated) = Show();

        DoubleClick(window, RowButton(window, list, 1));

        Assert.Empty(activated);
    });

    [Fact]
    public void DoubleClickingEmptySpaceBelowTheRows_ActivatesNothing() => Headless.Run(() =>
    {
        var (window, _, activated) = Show();

        DoubleClick(window, new Point(100, 280));

        Assert.Empty(activated);
    });

    [Fact]
    public void EnterOnASelectedRow_ActivatesIt() => Headless.Run(() =>
    {
        var (window, list, activated) = Show();
        list.SelectedIndex = 2;
        list.ContainerFromIndex(2)!.Focus();

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");

        Assert.Equal(["c"], activated);
    });

    [Fact]
    public void EnterOnAFocusedRowButton_PressesTheButtonOnly() => Headless.Run(() =>
    {
        var (window, list, activated) = Show();
        list.SelectedIndex = 0;
        var button = list.ContainerFromIndex(0)!.GetVisualDescendants().OfType<Button>().First();
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        button.Focus();

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");

        Assert.Equal(1, clicks);
        Assert.Empty(activated);
    });
}
