using Avalonia.Controls;
using Rambutan.App.Controls;

namespace Rambutan.App.Tests;

/// <summary>A visualizer stops polling while its window doesn't show, so the
/// analyzer's read lease lapses with it.</summary>
public class VisualizerWindowTests
{
    private static void WithWindow(Action<Window, VisualizerControl> body) => Headless.Run(() =>
    {
        var visualizer = new VisualizerControl { IsActive = true };
        var window = new Window { Content = visualizer, Width = 200, Height = 100 };
        window.Show();
        try
        {
            body(window, visualizer);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void AShownWindow_Polls() =>
        WithWindow((_, visualizer) => Assert.True(visualizer.IsPolling));

    [Fact]
    public void Minimizing_StopsIt_AndRestoringStartsIt() => WithWindow((window, visualizer) =>
    {
        window.WindowState = WindowState.Minimized;
        Assert.False(visualizer.IsPolling);

        window.WindowState = WindowState.Normal;
        Assert.True(visualizer.IsPolling);
    });

    [Fact]
    public void HidingTheWindow_StopsIt_AndShowingStartsIt() => WithWindow((window, visualizer) =>
    {
        window.Hide();
        Assert.False(visualizer.IsPolling);

        window.Show();
        Assert.True(visualizer.IsPolling);
    });

    [Fact]
    public void AnInactiveVisualizer_StaysStopped_WhenItsWindowRestores() => WithWindow((window, visualizer) =>
    {
        visualizer.IsActive = false;
        window.WindowState = WindowState.Minimized;
        window.WindowState = WindowState.Normal;

        Assert.False(visualizer.IsPolling);

        visualizer.IsActive = true;
        Assert.True(visualizer.IsPolling);
    });
}
