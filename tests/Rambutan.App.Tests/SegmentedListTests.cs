using Avalonia;
using Avalonia.Controls;

namespace Rambutan.App.Tests;

/// <summary>A segmented control on a scrolling page leaves the page's scroll alone.</summary>
public class SegmentedListTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    /// <summary>A page whose segmented control sits below the first screen, as
    /// Settings' streaming quality does in a short window.</summary>
    private static (Window Window, ScrollViewer Page, ListBox Segmented) Show(string classes)
    {
        ShellResources.Ensure();
        var segmented = new ListBox { ItemsSource = new[] { "High", "Data saver" }, SelectedIndex = 0 };
        segmented.Classes.AddRange(classes.Split(' '));
        var page = new ScrollViewer
        {
            Content = new StackPanel
            {
                Children =
                {
                    new Border { Height = 500 },
                    segmented,
                    new Border { Height = 900 },
                },
            },
        };
        var window = new Window { Width = 600, Height = 300, Content = page };
        window.Show();
        Settle(window);
        return (window, page, segmented);
    }

    [Fact]
    public void ItsSelection_DoesNotScrollThePage_WhenThePageOpens() => Headless.Run(() =>
    {
        var (_, page, _) = Show("segmented");

        Assert.Equal(0, page.Offset.Y);
    });

    [Fact]
    public void ASelectionMadeLater_DoesNotScrollThePageEither() => Headless.Run(() =>
    {
        var (window, page, segmented) = Show("segmented");

        segmented.SelectedIndex = 1;
        Settle(window);

        Assert.Equal(0, page.Offset.Y);
    });

    /// <summary>The control case: without the class, the list brings its
    /// selection into view, which is what moved the page.</summary>
    [Fact]
    public void APlainList_ScrollsThePageToItsSelection() => Headless.Run(() =>
    {
        var (_, page, _) = Show("plain");

        Assert.True(page.Offset.Y > 0);
    });
}
