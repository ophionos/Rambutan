using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>The title bar's search field: Ctrl+K, Enter and Esc, where it shows,
/// and that it doesn't move the window.</summary>
public class TitleSearchTests
{
    /// <summary>No SynchronizationContext, so a completed fake runs the view
    /// model's continuation inline (see SearchPageViewModelTests.Inline).</summary>
    private static void Inline(Action body)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            body();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>The window on Settings, a page with a field of its own.</summary>
    private static (MainWindow Window, MainViewModel Vm) Show(double width = 1280)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var vm = new MainViewModel();
        vm.Navigation.GoTo(new SettingsPageViewModel(), NavSection.Settings);
        var window = new MainWindow { DataContext = vm, Width = width, Height = 800 };
        window.Show();
        Settle(window);
        return (window, vm);
    }

    private static TextBox Field(Window window) =>
        window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "TitleSearch");

    private static object? Focused(Window window) => window.FocusManager!.GetFocusedElement();

    private static void CtrlK(Window window) =>
        window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, "k");

    [Fact]
    public void EnterInTheField_OpensSearch_RunsTheQuery_AndKeepsItInTheHistory()
    {
        var catalog = new FakeCatalog { Moods = () => Task.FromResult(Rambutan.Core.Models.MoodsPage.Empty) };
        var history = new SearchHistory(null);
        history.Add("justice");
        var navigation = new NavigationService(catalog, null!, null!, history);
        navigation.GoTo(new SettingsPageViewModel(), NavSection.Settings);

        Inline(() => navigation.SearchFor("daft punk"));

        var page = Assert.IsType<SearchPageViewModel>(navigation.Current);
        Assert.Equal(NavSection.Search, navigation.Section);
        Assert.Equal("daft punk", page.Query);
        var search = Assert.Single(catalog.Searches);
        Assert.Equal("daft punk", search.Query);
        Assert.Null(search.Filter);
        Assert.Equal(["daft punk", "justice"], history.Queries);
        Assert.Empty(page.Dropdown);
        Inline(() => search.Complete("Discovery"));
        Assert.True(page.HasSearched);
    }

    [Fact]
    public void CtrlK_FocusesTheField_AndSelectsItsText() => Headless.Run(() =>
    {
        var (window, _) = Show();
        var field = Field(window);
        Assert.True(field.IsEffectivelyVisible);
        Assert.Equal("Search YouTube Music", AutomationProperties.GetName(field));
        field.Text = "daft";

        CtrlK(window);

        Assert.Same(field, Focused(window));
        Assert.Equal(0, field.SelectionStart);
        Assert.Equal(4, field.SelectionEnd);
        window.Close();
    });

    [Fact]
    public void EnterWithText_OpensSearchWithTheQuery_AndEmptiesTheField() => Headless.Run(() =>
    {
        var (window, vm) = Show();
        var field = Field(window);
        CtrlK(window);
        field.Text = "daft punk";

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(window);

        var page = Assert.IsType<SearchPageViewModel>(vm.Navigation.Current);
        Assert.Equal("daft punk", page.Query);
        Assert.Equal("", field.Text ?? "");
        // The Search page has its own field: the title bar's hides, and the
        // page's has the focus.
        Assert.False(field.IsEffectivelyVisible);
        Assert.Equal("SearchBox", (Focused(window) as Control)?.Name);
        window.Close();
    });

    [Fact]
    public void EnterWithNothing_OpensSearch_WithTheFocusInItsField() => Headless.Run(() =>
    {
        var (window, vm) = Show();
        CtrlK(window);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(window);

        var page = Assert.IsType<SearchPageViewModel>(vm.Navigation.Current);
        Assert.Equal("", page.Query);
        Assert.Equal("SearchBox", (Focused(window) as Control)?.Name);
        window.Close();
    });

    [Fact]
    public void OnTheSearchPage_CtrlK_FocusesThePagesField() => Headless.Run(() =>
    {
        var (window, vm) = Show();
        vm.Navigation.GoSearch();
        Settle(window);
        Assert.False(Field(window).IsEffectivelyVisible);
        var home = window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "Home");
        home.Focus(NavigationMethod.Tab);

        CtrlK(window);
        Settle(window);

        Assert.Equal("SearchBox", (Focused(window) as Control)?.Name);
        window.Close();
    });

    [Fact]
    public void Esc_EmptiesTheField_ThenGivesTheFocusBackToThePage() => Headless.Run(() =>
    {
        var (window, vm) = Show();
        var filter = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "FilterBox");
        filter.Focus(NavigationMethod.Tab);
        vm.NowPlaying.IsOpen = false;
        CtrlK(window);
        var field = Field(window);
        field.Text = "daft";

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Equal("", field.Text ?? "");
        Assert.Same(field, Focused(window));

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Same(filter, Focused(window));
        window.Close();
    });

    [Fact]
    public void TheField_IsReachedByTab_AfterTheTitleBarsButtons() => Headless.Run(() =>
    {
        var (window, _) = Show();
        var toggle = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SidebarToggle");
        var items = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "TitleBarItems");
        toggle.Focus(NavigationMethod.Tab);

        var path = new List<object?>();
        for (var i = 0; i < 6 && Focused(window) is Visual v && items.IsVisualAncestorOf(v); i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            path.Add(Focused(window));
        }

        Assert.Same(Field(window), path[^1]);
        window.Close();
    });

    [Fact]
    public void TheField_HidesInTheMiniPlayer_AndWhereItDoesntFit() => Headless.Run(() =>
    {
        var (window, vm) = Show();
        var field = Field(window);
        Assert.True(field.IsEffectivelyVisible);

        vm.IsMiniPlayer = true;
        Settle(window);
        Assert.False(field.IsEffectivelyVisible);

        vm.IsMiniPlayer = false;
        Settle(window);
        Assert.True(field.IsEffectivelyVisible);

        // The window's minimum width (the player bar's) still has room for the
        // field at the default text size; TitleSearchFitTests cover the floor.
        window.Width = 700;
        Settle(window);
        var bar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TitleBar");
        var items = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "TitleBarItems");
        var fit = TitleSearchFit.Place(bar.Bounds.Width, items.Bounds.Right);
        Assert.Equal(fit is not null, field.IsEffectivelyVisible);
        if (fit is { } place)
        {
            Assert.Equal(place.Width, field.Bounds.Width, 0.5);
            Assert.Equal(place.Left, field.TranslatePoint(default, bar)!.Value.X, 0.5);
        }
        window.Close();
    });

    [Fact]
    public void ADoubleClickInTheField_DoesntMaximizeTheWindow() => Headless.Run(() =>
    {
        var (window, _) = Show();
        var field = Field(window);
        var inField = field.TranslatePoint(new Point(field.Bounds.Width / 2, field.Bounds.Height / 2), window)!.Value;

        for (var i = 0; i < 2; i++)
        {
            window.MouseDown(inField, MouseButton.Left);
            window.MouseUp(inField, MouseButton.Left);
        }
        Settle(window);

        Assert.Equal(WindowState.Normal, window.WindowState);

        // The rest of the bar still maximizes.
        var titleBar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TitleBar");
        var empty = titleBar.TranslatePoint(new Point(titleBar.Bounds.Width - 160, 20), window)!.Value;
        for (var i = 0; i < 2; i++)
        {
            window.MouseDown(empty, MouseButton.Left);
            window.MouseUp(empty, MouseButton.Left);
        }
        Settle(window);
        Assert.Equal(WindowState.Maximized, window.WindowState);
        window.Close();
    });

    [Fact]
    public void TheSearchPage_HasNoSearchButton() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var view = new Rambutan.App.Views.SearchPage { DataContext = new SearchPageViewModel(new FakeCatalog(), null!, null!) };
        var window = new Window { Width = 1000, Height = 700, DataContext = new MainViewModel(), Content = view };
        window.Show();
        Settle(window);

        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && (b.Content as string == "Search" || AutomationProperties.GetName(b) == "Search"));
        window.Close();
    });
}
