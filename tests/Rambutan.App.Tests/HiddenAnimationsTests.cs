using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Threading;
using Rambutan.App.Controls;

namespace Rambutan.App.Tests;

public class HiddenAnimationsTests
{
    private static (Window Window, StackPanel Holder) Show(Control subject)
    {
        var holder = new StackPanel { Children = { subject } };
        // A panel between: what hides the control is usually not its parent.
        var window = new Window { Width = 300, Height = 200, Content = new StackPanel { Children = { holder } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, holder);
    }

    private static ProgressBar LoadingBar()
    {
        var bar = new ProgressBar { IsIndeterminate = true };
        HiddenAnimations.SetStop(bar, true);
        return bar;
    }

    private static Border Bone()
    {
        var bone = new Border { Width = 40, Height = 10 };
        HiddenAnimations.SetStop(bone, true);
        return bone;
    }

    private static bool IsUnseen(Control control) => control.Classes.Contains(HiddenAnimations.UnseenClass);

    [Fact]
    public void HiddenBar_IsNotIndeterminate_UntilItShowsAgain() => Headless.Run(() =>
    {
        var bar = LoadingBar();
        var (window, holder) = Show(bar);
        Assert.True(bar.IsIndeterminate);

        holder.IsVisible = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(bar.IsIndeterminate);

        holder.IsVisible = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(bar.IsIndeterminate);

        // Its own IsVisible, as most pages hide theirs.
        bar.IsVisible = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(bar.IsIndeterminate);

        bar.IsVisible = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(bar.IsIndeterminate);
        window.Close();
    });

    [Fact]
    public void BarHiddenFromTheStart_BeginsStopped() => Headless.Run(() =>
    {
        var bar = LoadingBar();
        bar.IsVisible = false;
        var (window, _) = Show(bar);
        Assert.False(bar.IsIndeterminate);

        bar.IsVisible = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(bar.IsIndeterminate);
        window.Close();
    });

    [Fact]
    public void BarMadeDeterminateWhileHidden_StaysDeterminate() => Headless.Run(() =>
    {
        var bar = LoadingBar();
        var (window, holder) = Show(bar);
        holder.IsVisible = false;
        Dispatcher.UIThread.RunJobs();

        bar.IsIndeterminate = false;
        holder.IsVisible = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(bar.IsIndeterminate);
        window.Close();
    });

    [Fact]
    public void BarMadeIndeterminateWhileHidden_WaitsUntilItShows() => Headless.Run(() =>
    {
        var bar = new ProgressBar();
        HiddenAnimations.SetStop(bar, true);
        var (window, holder) = Show(bar);
        holder.IsVisible = false;
        Dispatcher.UIThread.RunJobs();

        bar.IsIndeterminate = true;
        Assert.False(bar.IsIndeterminate);

        holder.IsVisible = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(bar.IsIndeterminate);
        window.Close();
    });

    [Fact]
    public void DeterminateBar_IsLeftAsItIs() => Headless.Run(() =>
    {
        var bar = new ProgressBar { Value = 40 };
        HiddenAnimations.SetStop(bar, true);
        var (window, holder) = Show(bar);

        holder.IsVisible = false;
        Dispatcher.UIThread.RunJobs();
        holder.IsVisible = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(bar.IsIndeterminate);
        Assert.Equal(40, bar.Value);
        window.Close();
    });

    [Fact]
    public void OtherControl_IsUnseenWhileAnythingHidesIt() => Headless.Run(() =>
    {
        var bone = Bone();
        var (window, holder) = Show(bone);
        Assert.False(IsUnseen(bone));

        holder.IsVisible = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsUnseen(bone));

        holder.IsVisible = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(IsUnseen(bone));

        bone.IsVisible = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsUnseen(bone));
        window.Close();
    });

    [Fact]
    public void OutOfTheTree_CountsAsHidden() => Headless.Run(() =>
    {
        // Styled before it is shown, as a page being built is.
        var bone = Bone();
        var bar = LoadingBar();
        Assert.True(IsUnseen(bone));
        Assert.False(bar.IsIndeterminate);

        var panel = new StackPanel { Children = { bone, bar } };
        var (window, holder) = Show(panel);
        Assert.False(IsUnseen(bone));
        Assert.True(bar.IsIndeterminate);

        // Taken away, as a page navigated from is.
        holder.Children.Remove(panel);
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsUnseen(bone));
        Assert.False(bar.IsIndeterminate);

        holder.Children.Add(panel);
        Dispatcher.UIThread.RunJobs();
        Assert.False(IsUnseen(bone));
        Assert.True(bar.IsIndeterminate);
        window.Close();
    });

    [Fact]
    public void NotAskedFor_NothingChanges() => Headless.Run(() =>
    {
        var bar = new ProgressBar { IsIndeterminate = true };
        var bone = new Border();
        // The app's styles may be loaded by another test; these opt out.
        HiddenAnimations.SetStop(bar, false);
        var (window, holder) = Show(new StackPanel { Children = { bar, bone } });

        holder.IsVisible = false;
        Dispatcher.UIThread.RunJobs();

        Assert.True(bar.IsIndeterminate);
        Assert.False(IsUnseen(bone));
        window.Close();
    });

    [Fact]
    public void AppStyles_StopTheirLoadingBarsAndSkeletons() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var bar = new ProgressBar { IsIndeterminate = true };
        var bone = new Border { Classes = { "skeleton" } };
        var (window, holder) = Show(new StackPanel { Children = { bar, bone } });
        Assert.True(HiddenAnimations.GetStop(bar));
        Assert.True(HiddenAnimations.GetStop(bone));
        Assert.True(bar.IsIndeterminate);
        Assert.False(IsUnseen(bone));

        holder.IsVisible = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(bar.IsIndeterminate);
        Assert.True(IsUnseen(bone));
        window.Close();
    });

    /// <summary>An endless animation whose selector still matches a hidden control
    /// keeps the UI thread awake (<see cref="HiddenAnimations"/>), so each one's
    /// selector has to leave hidden controls out.</summary>
    [Fact]
    public void EveryEndlessAnimation_LeavesHiddenControlsOut()
    {
        var appDir = Path.Combine(RepoRoot(), "src", "Rambutan.App");
        var found = 0;
        foreach (var file in Directory.EnumerateFiles(appDir, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match endless in Regex.Matches(text, "IterationCount=\"Infinite\""))
            {
                found++;
                var styles = Regex.Matches(text[..endless.Index], "<Style\\s+Selector=\"([^\"]*)\"");
                Assert.True(styles.Count > 0, $"{file}: an endless animation outside a style");
                var selector = styles[^1].Groups[1].Value;
                Assert.True(
                    selector.Contains($":not(.{HiddenAnimations.UnseenClass})") || selector.Contains("[IsVisible=True]"),
                    $"{Path.GetFileName(file)}: the endless animation of '{selector}' runs on hidden controls");
            }
        }

        // The skeleton's pulse and the spinner, at least: the scan found the styles.
        Assert.True(found >= 2, $"found {found} endless animations");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rambutan.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Can't find the repository root from " + AppContext.BaseDirectory);
    }
}
