using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Rambutan.App.Controls;

namespace Rambutan.App.Tests;

/// <summary>Playlist row numbers: they follow the row's position, not the order the
/// containers happened to be made in.</summary>
public class RowNumberTests
{
    private static (Window Window, ListBox List) Show(ObservableCollection<string> items)
    {
        var list = new ListBox
        {
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<string>((_, _) => new TextBlock { Height = 20 }),
        };
        RowNumber.SetIsEnabled(list, true);
        var window = new Window { Width = 300, Height = 600, Content = list };
        window.Show();
        window.UpdateLayout();
        return (window, list);
    }

    private static int[] Numbers(ListBox list) =>
        Enumerable.Range(0, list.ItemCount)
            .Select(i => list.ContainerFromIndex(i) is { } c ? RowNumber.GetNumber(c) : -1)
            .ToArray();

    [Fact]
    public void Rows_AreNumberedFromOne() => Headless.Run(() =>
    {
        var (window, list) = Show(["a", "b", "c"]);

        Assert.Equal([1, 2, 3], Numbers(list));
        window.Close();
    });

    [Fact]
    public void AnAppendedPage_ContinuesTheCount() => Headless.Run(() =>
    {
        var items = new ObservableCollection<string> { "a", "b" };
        var (window, list) = Show(items);

        items.Add("c");
        items.Add("d");
        window.UpdateLayout();

        Assert.Equal([1, 2, 3, 4], Numbers(list));
        window.Close();
    });

    [Fact]
    public void ARemovedRow_RenumbersTheRowsAfterIt() => Headless.Run(() =>
    {
        var items = new ObservableCollection<string> { "a", "b", "c", "d" };
        var (window, list) = Show(items);

        items.RemoveAt(1);
        window.UpdateLayout();

        Assert.Equal([1, 2, 3], Numbers(list));
        window.Close();
    });
}
