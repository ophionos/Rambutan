using Avalonia;
using Rambutan.App.Views;

namespace Rambutan.App.Tests;

/// <summary>The shell stays inside the window's visible frame.</summary>
public class ShellMarginTests
{
    [Fact]
    public void DrawnDecorations_KeepClearOfFrameAndShadow_ButNotOfTheTitleBar() =>
        // Wayland: an 8 px shadow on every side, the 40 px title bar on top.
        Assert.Equal(new Thickness(8), ShellMargin.For(default, new Thickness(8, 48, 8, 8), 40));

    [Fact]
    public void PlatformDecorations_ReportingOnlyTheTitleBar_LeaveNoMargin() =>
        // Windows: the title bar's height on top and nothing else.
        Assert.Equal(default, ShellMargin.For(default, new Thickness(0, 32, 0, 0), 40));

    [Fact]
    public void MaximizedOnWindows_KeepsTheOffScreenFrame() =>
        Assert.Equal(new Thickness(7), ShellMargin.For(new Thickness(7), new Thickness(0, 40, 0, 0), 40));

    [Fact]
    public void TitleBarTallerThanTheReportedTop_NeverGoesNegative() =>
        Assert.Equal(new Thickness(1, 0, 1, 1), ShellMargin.For(default, new Thickness(1, 30, 1, 1), 40));
}
