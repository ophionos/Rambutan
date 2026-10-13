using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Tests;

/// <summary>Scroll position survives the page's view being swapped out, and the mouse's side buttons walk the history.</summary>
public class ScrollMemoryTests
{
    private sealed class Page : ViewModelBase
    {
        public string[] Rows { get; } = Enumerable.Range(0, 200).Select(i => $"row {i}").ToArray();
    }

    /// <summary>A shell like MainWindow's: a ContentControl whose pages are built
    /// from a data template, each a list that remembers its scroll.</summary>
    private static (Window Window, ContentControl Host) Show(bool recycle = true)
    {
        var host = new ContentControl
        {
            // Recycling, as XAML DataTemplates do: album to album reuses the view
            // (the save happens on DataContextChanged). Without it, a new view is
            // built and the old one saves as it leaves the tree.
            ContentTemplate = new FuncDataTemplate<Page>((_, _) =>
            {
                var list = new ListBox
                {
                    [!ItemsControl.ItemsSourceProperty] = new Avalonia.Data.Binding(nameof(Page.Rows)),
                    ItemTemplate = new FuncDataTemplate<string>((_, _) =>
                        new TextBlock { Height = 30, [!TextBlock.TextProperty] = new Avalonia.Data.Binding() }),
                };
                ScrollMemory.SetIsEnabled(list, true);
                return list;
            }, supportsRecycling: recycle),
        };
        var window = new Window { Width = 400, Height = 300, Content = host };
        window.Show();
        window.UpdateLayout();
        return (window, host);
    }

    private static ScrollViewer Viewer(ContentControl host) =>
        (ScrollViewer)((ListBox)host.Presenter!.Child!).Scroll!;

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReturningToAPage_RestoresWhereItWasLeft(bool recycle) => Headless.Run(() =>
    {
        var (window, host) = Show(recycle);
        var first = new Page();
        host.Content = first;
        Settle(window);
        Viewer(host).Offset = new Vector(0, 900);
        Settle(window);

        host.Content = new Page();
        Settle(window);
        Assert.Equal(900, first.SavedScrollOffset);

        host.Content = first;
        Settle(window);

        Assert.Equal(900, Viewer(host).Offset.Y);
    });

    [Fact]
    public void ARecycledView_DoesNotCarryTheLastPagesOffsetIntoANewPage() => Headless.Run(() =>
    {
        var (window, host) = Show();
        host.Content = new Page();
        Settle(window);
        Viewer(host).Offset = new Vector(0, 900);
        Settle(window);

        host.Content = new Page();
        Settle(window);

        Assert.Equal(0, Viewer(host).Offset.Y);
    });

    [Fact]
    public void MouseSideButtons_GoBackAndForward() => Headless.Run(() =>
    {
        var nav = new NavigationService(new FakeCatalog(), null!, new TrackActionsViewModel());
        var root = new Page();
        nav.GoTo(root, NavSection.Home);
        nav.OpenAlbum("MPREb_a");
        var album = nav.Current;

        // A list under the pointer marks presses handled; the tunnel handler runs first.
        var (window, _) = Show();
        HistoryButtons.Attach(window, nav);
        var at = new Point(100, 100);

        window.MouseDown(at, MouseButton.XButton1);
        window.MouseUp(at, MouseButton.XButton1);
        Assert.Same(root, nav.Current);

        window.MouseDown(at, MouseButton.XButton2);
        window.MouseUp(at, MouseButton.XButton2);
        Assert.Same(album, nav.Current);

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Assert.Same(album, nav.Current);
    });
}
