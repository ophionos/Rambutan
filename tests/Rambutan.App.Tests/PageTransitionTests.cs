using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Animation;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;

namespace Rambutan.App.Tests;

public class PageTransitionTests
{
    private static (Window Window, TransitioningContentControl Host) Build(bool reduced)
    {
        ShellResources.Ensure();
        var host = new TransitioningContentControl { Classes = { "pagehost" } };
        var window = new Window { Width = 400, Height = 300, Content = host };
        if (reduced)
            window.Classes.Add("reducedmotion");
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, host);
    }

    // Animations only move on a rendered frame: a tick of the render timer alone does not carry them.
    private static void Pump(Window window, Func<bool> until)
    {
        for (var i = 0; i < 200 && !until(); i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.InvalidateVisual();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Host_CrossfadesFor120Ms_OrCutsUnderReducedMotion() => Headless.Run(() =>
    {
        var (window, host) = Build(reduced: false);
        var fade = Assert.IsType<CrossFade>(host.PageTransition);
        Assert.Equal(TimeSpan.FromMilliseconds(120), fade.Duration);
        Assert.Equal(PageTransitions.Fade, fade.Duration);

        window.Classes.Add("reducedmotion");
        Assert.Null(host.PageTransition);

        window.Classes.Remove("reducedmotion");
        Assert.IsType<CrossFade>(host.PageTransition);
        window.Close();
    });

    [Fact]
    public void Host_UnderReducedMotion_SwapsAtOnce() => Headless.Run(() =>
    {
        var (window, host) = Build(reduced: true);
        var first = new Border { Name = "first" };
        var second = new Border { Name = "second" };
        host.Content = first;
        Dispatcher.UIThread.RunJobs();
        host.Content = second;
        Dispatcher.UIThread.RunJobs();

        Assert.True(second.IsAttachedToVisualTree());
        Assert.False(first.IsAttachedToVisualTree());
        window.Close();
    });

    [Fact]
    public void Host_AfterTheFade_TheOldPageIsOutOfTheTree() => Headless.Run(() =>
    {
        var (window, host) = Build(reduced: false);
        var first = new Border { Name = "first" };
        var second = new Border { Name = "second" };
        host.Content = first;
        Dispatcher.UIThread.RunJobs();
        var completed = 0;
        host.TransitionCompleted += (_, _) => completed++;
        Pump(window, () => completed == 1);

        host.Content = second;
        Dispatcher.UIThread.RunJobs();
        Assert.True(first.IsAttachedToVisualTree());
        Assert.True(second.IsAttachedToVisualTree());
        Pump(window, () => completed == 2);

        Assert.True(second.IsAttachedToVisualTree());
        Assert.False(first.IsAttachedToVisualTree());
        window.Close();
    });

    [Fact]
    public void Host_NavigatingAgainDuringTheFade_KeepsNoStalePage() => Headless.Run(() =>
    {
        var (window, host) = Build(reduced: false);
        var pages = Enumerable.Range(0, 4).Select(i => new Border { Name = $"page{i}" }).ToArray();
        host.Content = pages[0];
        Dispatcher.UIThread.RunJobs();

        host.Content = pages[1];
        Dispatcher.UIThread.RunJobs();
        host.Content = pages[2];
        Dispatcher.UIThread.RunJobs();
        host.Content = pages[3];
        Dispatcher.UIThread.RunJobs();
        Assert.False(pages[0].IsAttachedToVisualTree());
        Pump(window, () => pages.Take(3).All(p => !p.IsAttachedToVisualTree()));

        Assert.True(pages[3].IsAttachedToVisualTree());
        Assert.All(pages.Take(3), p => Assert.False(p.IsAttachedToVisualTree()));
        window.Close();
    });

    [Fact]
    public void Host_TheLeavingPageTakesNoPointerInput_TheArrivingPageDoes() => Headless.Run(() =>
    {
        var (window, host) = Build(reduced: false);
        var first = new Border { Name = "first" };
        var second = new Border { Name = "second" };
        host.Content = first;
        Dispatcher.UIThread.RunJobs();
        host.Content = second;
        Dispatcher.UIThread.RunJobs();

        bool Hittable(Control page) =>
            page.GetVisualAncestors().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First().IsHitTestVisible;
        Assert.False(Hittable(first));
        Assert.True(Hittable(second));
        window.Close();
    });

    [Fact]
    public void Host_TheArrivingPageCanTakeFocusAtOnce() => Headless.Run(() =>
    {
        var (window, host) = Build(reduced: false);
        host.Content = new Border { Child = new TextBox { Name = "old" } };
        Dispatcher.UIThread.RunJobs();
        var box = new TextBox { Name = "search" };
        host.Content = new Border { Child = box };
        Dispatcher.UIThread.RunJobs();

        Assert.True(box.Focus());
        Assert.True(box.IsFocused);
        window.Close();
    });
}
