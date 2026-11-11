using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Rambutan.App.Views;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>A shelf's page arrows are big enough to hit and stay controls at the
/// ends of the row.</summary>
public class ShelfPagingButtonTests
{
    private static (Window Window, Button Back, Button Forward) Show(int cards)
    {
        ShellResources.Ensure();
        var shelf = new Shelf("New releases", [.. Enumerable.Range(0, cards).Select(i => FakeCatalog.Entry($"Card {i}"))]);
        var view = new ShelfView { DataContext = shelf };
        var window = new Window { Width = 600, Height = 400, Content = view };
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("shelfpage")).ToList();
        return (window, buttons.Single(b => b.Name == "PageBack"), buttons.Single(b => b.Name == "PageForward"));
    }

    [Fact]
    public void AnOverflowingShelf_ShowsBothArrowsAtLeast32Square() => Headless.Run(() =>
    {
        var (window, back, forward) = Show(cards: 12);

        Assert.True(back.IsVisible && forward.IsVisible);
        Assert.All(new[] { back, forward }, b =>
        {
            Assert.True(b.Bounds.Width >= 32, $"{b.Name} is {b.Bounds.Width} wide");
            Assert.True(b.Bounds.Height >= 32, $"{b.Name} is {b.Bounds.Height} high");
        });
        window.Close();
    });

    [Fact]
    public void TheArrowAtTheEnd_IsDisabledButKeepsItsDiscAndFullOpacity() => Headless.Run(() =>
    {
        var (window, back, forward) = Show(cards: 12);

        // At the start the back arrow can't page; it still reads as a control.
        Assert.False(back.IsEnabled);
        Assert.True(forward.IsEnabled);
        Assert.Equal(1, back.Opacity);
        var disc = back.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        Assert.NotNull(disc.Background);
        Assert.Equal(((Avalonia.Media.ISolidColorBrush)disc.Background!).Color,
            ((Avalonia.Media.ISolidColorBrush)forward.GetVisualDescendants().OfType<ContentPresenter>()
                .First(p => p.Name == "PART_ContentPresenter").Background!).Color);
        window.Close();
    });

    [Fact]
    public void AShelfThatFits_ShowsNoArrows() => Headless.Run(() =>
    {
        var (window, back, forward) = Show(cards: 2);

        Assert.False(back.IsVisible);
        Assert.False(forward.IsVisible);
        window.Close();
    });
}
