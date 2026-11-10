using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Settings;

namespace Rambutan.App.Tests;

/// <summary>The Settings page's section list and filter.</summary>
public class SettingsNavigationTests
{
    internal const double Wide = 1100;
    private const double Narrow = 800;

    internal sealed record Shown(Window Window, SettingsPage View)
    {
        public ListBox List => View.FindControl<ListBox>("SectionList")!;
        public ScrollViewer Scroller => View.FindControl<ScrollViewer>("Scroller")!;
        public StackPanel Column => View.FindControl<StackPanel>("Column")!;
        public TextBox Filter => View.FindControl<TextBox>("FilterBox")!;
        public Control NoMatch => View.FindControl<Control>("NoMatch")!;

        public string[] Entries => List.Items.Cast<string>().ToArray();

        public TextBlock[] VisibleHeadings =>
            View.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("settingsheader") && t.IsEffectivelyVisible)
                .ToArray();

        public TextBlock Heading(string text) =>
            View.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Classes.Contains("settingsheader") && t.Text == text);

        public Border Row(string label) =>
            View.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Classes.Contains("settinglabel") && t.Text == label)
                .GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("settingrow"));

        public Border[] VisibleRows =>
            View.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("settingrow") && b.IsEffectivelyVisible)
                .ToArray();

        public double TopInContent(Visual v) => v.TranslatePoint(default, (Visual)Scroller.Content!)!.Value.Y;

        public void Settle()
        {
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public void Type(string text)
        {
            Filter.Text = text;
            Settle();
        }
    }

    internal static Shown Show(double width, double height = 800, Func<SettingsPageViewModel>? create = null)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var page = create?.Invoke() ?? Page();
        var view = new SettingsPage { DataContext = page };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Classes.Add("reducedmotion");
        window.Show();
        var shown = new Shown(window, view);
        shown.Settle();
        return shown;
    }

    private static SettingsPageViewModel Page(Action<bool>? taskbar = null) =>
        new(new PlaybackSettings { CrossfadeEnabled = true }, new AppSettings(), new AppearanceSettings(),
            new Rambutan.Integrations.UpdateService(null), new SessionViewModel(), () => { })
        {
            ApplyTaskbarControls = taskbar,
        };

    private static bool InAutomationTree(Control root, Control target)
    {
        return Walk(ControlAutomationPeer.CreatePeerForElement(root));

        bool Walk(AutomationPeer peer) =>
            peer.GetChildren().Any(c => c is ControlAutomationPeer { Owner: var owner } && owner == target || Walk(c));
    }

    [Fact]
    public void TheList_ShowsBesideTheColumnWhenWide_AndIsGoneWhenNarrow_WithTheColumnInPlace() => Headless.Run(() =>
    {
        var wide = Show(Wide);
        var narrow = Show(Narrow);

        Assert.True(wide.List.IsEffectivelyVisible);
        Assert.True(InAutomationTree(wide.View, wide.List));
        Assert.False(narrow.List.IsVisible);
        Assert.False(InAutomationTree(narrow.View, narrow.List));

        var wideX = wide.Column.TranslatePoint(default, wide.View)!.Value.X;
        var narrowX = narrow.Column.TranslatePoint(default, narrow.View)!.Value.X;
        Assert.Equal(wideX, narrowX);
        Assert.Equal(SettingsPage.ColumnWidth, wide.Column.Bounds.Width);
        Assert.Equal(wide.Column.Bounds.Width, narrow.Column.Bounds.Width);

        // Beside the column, not over it.
        var listX = wide.List.TranslatePoint(default, wide.View)!.Value.X;
        Assert.True(listX >= wideX + wide.Column.Bounds.Width + 24, $"list at {listX}");
        Assert.True(listX + wide.List.Bounds.Width <= Wide, "the list runs off the page");

        // The filter stays in both.
        Assert.True(wide.Filter.IsEffectivelyVisible);
        Assert.True(narrow.Filter.IsEffectivelyVisible);
        wide.Window.Close();
        narrow.Window.Close();
    });

    [Fact]
    public void TheThreshold_LeavesRoomForColumnGapAndList() => Headless.Run(() =>
    {
        var justWide = Show(SettingsPage.SectionListMinWidth);
        var justNarrow = Show(SettingsPage.SectionListMinWidth - 1);

        Assert.True(justWide.List.IsVisible);
        Assert.False(justNarrow.List.IsVisible);
        justWide.Window.Close();
        justNarrow.Window.Close();
    });

    [Fact]
    public void TheEntries_AreTheShownHeadings_InOrder() => Headless.Run(() =>
    {
        var s = Show(Wide);

        var headings = s.VisibleHeadings.Select(h => h.Text).ToArray();
        Assert.Contains("Playback", headings);
        // Sections whose view model is missing (downloads, the remote…) hide
        // themselves, and have no entry.
        Assert.True(headings.Length < 14);
        Assert.Equal(headings, s.Entries);
        Assert.Equal(0, s.List.SelectedIndex);
        s.Window.Close();
    });

    [Fact]
    public void ChoosingAnEntryWithEnter_PutsItsHeadingAtTheTop_AndFocusesIt() => Headless.Run(() =>
    {
        var s = Show(Wide);
        var index = Array.IndexOf(s.Entries, "Appearance");
        Assert.True(index > 0);

        var item = (ListBoxItem)s.List.ContainerFromIndex(index)!;
        item.Focus(NavigationMethod.Tab);
        s.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        s.Settle();

        var heading = s.Heading("Appearance");
        Assert.Equal(s.TopInContent(heading), s.Scroller.Offset.Y, 0.5);
        Assert.Same(heading, s.Window.FocusManager!.GetFocusedElement());
        Assert.Equal(index, s.List.SelectedIndex);
        Assert.Equal(2, AutomationProperties.GetHeadingLevel(heading));
        s.Window.Close();
    });

    [Fact]
    public void ClickingAnEntry_ScrollsToItsSection() => Headless.Run(() =>
    {
        var s = Show(Wide);
        var index = Array.IndexOf(s.Entries, "Behavior");
        var item = (ListBoxItem)s.List.ContainerFromIndex(index)!;
        var centre = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), s.Window)!.Value;

        s.Window.MouseDown(centre, MouseButton.Left);
        s.Window.MouseUp(centre, MouseButton.Left);
        s.Settle();

        var heading = s.Heading("Behavior");
        Assert.Equal(s.TopInContent(heading), s.Scroller.Offset.Y, 0.5);
        Assert.Same(heading, s.Window.FocusManager!.GetFocusedElement());
        Assert.Equal(index, s.List.SelectedIndex);
        s.Window.Close();
    });

    [Fact]
    public void ArrowsMoveAlongTheList_WithoutScrollingThePage() => Headless.Run(() =>
    {
        var s = Show(Wide);
        ((ListBoxItem)s.List.ContainerFromIndex(0)!).Focus(NavigationMethod.Tab);

        s.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        s.Settle();

        Assert.Equal(0, s.Scroller.Offset.Y);
        Assert.Same(s.List.ContainerFromIndex(1), s.Window.FocusManager!.GetFocusedElement());
        s.Window.Close();
    });

    [Fact]
    public void ScrollingThePage_MarksTheSectionInView_WithoutMovingFocus() => Headless.Run(() =>
    {
        var s = Show(Wide);
        s.Filter.Focus();
        var behavior = s.Heading("Behavior");

        s.Scroller.Offset = new Vector(0, s.TopInContent(behavior) + 20);
        s.Settle();

        Assert.Equal(Array.IndexOf(s.Entries, "Behavior"), s.List.SelectedIndex);
        Assert.Same(s.Filter, s.Window.FocusManager!.GetFocusedElement());
        Assert.Equal(s.TopInContent(behavior) + 20, s.Scroller.Offset.Y, 0.5);

        // At the end of the scroll, the last section.
        s.Scroller.Offset = new Vector(0, s.Scroller.Extent.Height);
        s.Settle();
        Assert.Equal(s.Entries.Length - 1, s.List.SelectedIndex);
        Assert.Same(s.Filter, s.Window.FocusManager!.GetFocusedElement());
        s.Window.Close();
    });

    [Fact]
    public void AFilter_HidesRowsAndEmptySections_AndClearingRestoresEveryRow() => Headless.Run(() =>
    {
        var s = Show(Wide);
        var before = s.VisibleRows;

        s.Type("crossfade");

        Assert.True(s.Row("Crossfade between tracks").IsEffectivelyVisible);
        // Its sub-rows go with it.
        Assert.True(s.Row("Crossfade duration").IsEffectivelyVisible);
        Assert.False(s.Row("Normalize volume across tracks").IsEffectivelyVisible);
        Assert.False(s.Heading("Behavior").IsEffectivelyVisible);
        Assert.Equal(["Playback"], s.Entries);
        Assert.False(s.NoMatch.IsVisible);

        s.Type("");

        Assert.Equal(before, s.VisibleRows);
        Assert.Equal(s.VisibleHeadings.Select(h => h.Text), s.Entries);
        s.Window.Close();
    });

    [Fact]
    public void ASectionsHeading_ShowsAllItsRows_AndWordsMayMixHeadingAndRow() => Headless.Run(() =>
    {
        var s = Show(Wide);

        s.Type("BEHAVIOR");
        Assert.Equal(["Behavior"], s.Entries);
        Assert.True(s.Row("Closing the window keeps Rambutan running in the tray").IsEffectivelyVisible);

        s.Type("playback normalize");
        Assert.True(s.Row("Normalize volume across tracks").IsEffectivelyVisible);
        Assert.False(s.Row("Crossfade between tracks").IsEffectivelyVisible);
        s.Window.Close();
    });

    [Fact]
    public void AFilterThatMatchesNothing_SaysSo() => Headless.Run(() =>
    {
        var s = Show(Wide);

        s.Type("zqxjv");

        Assert.True(s.NoMatch.IsEffectivelyVisible);
        Assert.False(s.Column.IsVisible);
        var hint = s.View.FindControl<TextBlock>("NoMatchHint")!;
        Assert.Contains("zqxjv", hint.Text);
        Assert.Empty(s.Entries);
        Assert.False(s.List.IsVisible);

        s.Filter.Focus();
        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        s.Settle();
        Assert.False(s.NoMatch.IsVisible);
        s.Window.Close();
    });

    [Fact]
    public void EscInTheField_ClearsTheFilter() => Headless.Run(() =>
    {
        var s = Show(Wide);
        s.Filter.Focus();
        s.Type("lyrics");
        Assert.Contains("Lyrics", s.Entries);
        Assert.DoesNotContain("Behavior", s.Entries);

        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        s.Settle();

        Assert.Equal("", s.Filter.Text);
        Assert.Equal(s.VisibleHeadings.Select(h => h.Text), s.Entries);
        Assert.Contains("Behavior", s.Entries);
        s.Window.Close();
    });

    [Fact]
    public void ARowHiddenByItsOwnBinding_StaysHidden_AndOneShownReturns() => Headless.Run(() =>
    {
        // The output row shows only with an output device model, which this
        // page has none of; the taskbar row shows because it has a callback.
        var s = Show(Wide, create: () => Page(taskbar: _ => { }));
        var output = s.Row("Output device");
        var taskbar = s.Row("Media controls on the taskbar");
        Assert.False(output.IsVisible);
        Assert.True(taskbar.IsEffectivelyVisible);

        s.Type("output");
        Assert.False(output.IsVisible);
        s.Type("lyrics");
        Assert.False(taskbar.IsVisible);
        Assert.False(output.IsVisible);

        s.Type("");
        Assert.False(output.IsVisible);
        Assert.True(taskbar.IsEffectivelyVisible);
        s.Window.Close();
    });

    [Fact]
    public void TheFilterAndTheList_HaveSpokenNames() => Headless.Run(() =>
    {
        var s = Show(Wide);

        static string? Spoken(Control c) => ControlAutomationPeer.CreatePeerForElement(c).GetName();
        Assert.Equal("Filter settings", Spoken(s.Filter));
        Assert.Equal("Sections", Spoken(s.List));
        s.Type("x");
        var clear = s.View.FindControl<Button>("ClearFilterButton")!;
        Assert.True(clear.IsEffectivelyVisible);
        Assert.Equal("Clear the filter", Spoken(clear));
        s.Window.Close();
    });
}

/// <summary>The section list and the filter after a language change; in the
/// language-switch collection, since the language is global.</summary>
[Collection(nameof(LanguageSwitch))]
public class SettingsNavigationLanguageTests
{
    [Fact]
    public void TheEntriesAndTheFilter_FollowALanguageChange() => Headless.Run(() =>
    {
        var s = SettingsNavigationTests.Show(SettingsNavigationTests.Wide);
        s.Type("crossfade");
        try
        {
            Rambutan.App.Localization.Strings.SetLanguage(Rambutan.App.Localization.Strings.PseudoLocale);
            s.Settle();

            // The pseudo-locale accents every letter: the English word no longer matches.
            Assert.True(s.NoMatch.IsVisible);
            s.Type("");
            Assert.Equal(s.VisibleHeadings.Select(h => h.Text), s.Entries);
            Assert.DoesNotContain("Playback", s.Entries);
        }
        finally
        {
            Rambutan.App.Localization.Strings.SetLanguage("en");
            s.Settle();
        }
        Assert.Contains("Playback", s.Entries);
        s.Window.Close();
    });
}
