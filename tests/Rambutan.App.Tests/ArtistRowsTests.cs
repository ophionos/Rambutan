using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>
/// The artist page's top-song rows hide their actions at rest, like every other
/// track list. They are an ItemsControl with no selection, so each row is a
/// focus stop instead: hover or focus in the row shows its actions, the arrows
/// reach them, and Enter plays from the focused row.
/// </summary>
public class ArtistRowsTests
{
    private static TrackInfo Track(string id, string title) => new()
    {
        VideoId = id,
        Title = title,
        Artists = [new("Daft Punk", "UCdp")],
    };

    private sealed record Page(Window Window, Button Before, ItemsControl List, Grid[] Rows, QueueModel Queue);

    private static Page Open()
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var catalog = new FakeCatalog
        {
            Artist = new ArtistPage
            {
                BrowseId = "UCdp", Name = "Daft Punk",
                TopSongs = [Track("a", "One More Time"), Track("b", "Around the World")],
                Shelves = [],
            },
        };
        var queue = new QueueModel();
        var vm = new ArtistPageViewModel(catalog, FakePlayback.Coordinator(queue), null!, "UCdp");
        vm.LoadAsync().GetAwaiter().GetResult();
        var view = new Rambutan.App.Views.ArtistPage { DataContext = vm };
        var before = new Button { Content = "before" };
        var window = new Window
        {
            Width = 1000, Height = 700, DataContext = new MainViewModel(),
            Content = new DockPanel { Children = { before, view } },
        };
        DockPanel.SetDock(before, Dock.Top);
        window.Show();
        window.UpdateLayout();

        var list = view.GetVisualDescendants().OfType<ItemsControl>()
            .Single(i => AutomationProperties.GetName(i) == "Top songs");
        var rows = list.GetVisualDescendants().OfType<Grid>().Where(g => TrackMenu.GetTrack(g) is not null).ToArray();
        return new Page(window, before, list, rows, queue);
    }

    private static Button[] ShownActions(Grid row) =>
        row.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Classes.Contains("rowactions"))
            is { IsEffectivelyVisible: true } actions
            ? actions.GetVisualDescendants().OfType<Button>().ToArray()
            : [];

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void AtRest_NoRowShowsItsActions_AndEachRowIsNamed() => Headless.Run(() =>
    {
        var page = Open();

        Assert.Equal(2, page.Rows.Length);
        Assert.All(page.Rows, row => Assert.Empty(ShownActions(row)));
        Assert.Equal("One More Time, Daft Punk", AutomationProperties.GetName(page.Rows[0]));
        // A focus stop a screen reader can land on: a named list item, not a bare panel.
        var peer = ControlAutomationPeer.CreatePeerForElement(page.Rows[0]);
        Assert.True(peer.IsControlElement());
        Assert.Equal(AutomationControlType.ListItem, peer.GetAutomationControlType());
        Assert.Equal("One More Time, Daft Punk", peer.GetName());
        page.Window.Close();
    });

    [Fact]
    public void Tab_EntersAtTheFirstRow_WhichThenShowsItsActions() => Headless.Run(() =>
    {
        var page = Open();

        // Tab past the header's buttons until focus enters the list.
        page.Before.Focus(NavigationMethod.Tab);
        for (var i = 0; i < 20 && !page.List.IsKeyboardFocusWithin; i++)
            page.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Settle(page.Window);

        Assert.True(page.Rows[0].IsFocused);
        var names = ShownActions(page.Rows[0]).Select(AutomationProperties.GetName).ToArray();
        Assert.Contains("Play from here", names);
        Assert.Contains("Add to queue", names);
        Assert.Empty(ShownActions(page.Rows[1]));
        page.Window.Close();
    });

    [Fact]
    public void Arrows_GoFromARowOntoItsActions_AndDownToTheNextRow() => Headless.Run(() =>
    {
        var page = Open();
        page.Rows[0].Focus(NavigationMethod.Tab);
        Settle(page.Window);

        var play = ShownActions(page.Rows[0])[0];
        Assert.Equal("Play from here", AutomationProperties.GetName(play));
        Assert.Same(play, RovingFocus.Target(page.List, page.Rows[0], Key.Right));
        Assert.Same(page.Rows[1], RovingFocus.Target(page.List, page.Rows[0], Key.Down));

        // Focus on one of its actions keeps them shown.
        play.Focus(NavigationMethod.Directional);
        Settle(page.Window);
        Assert.NotEmpty(ShownActions(page.Rows[0]));
        Assert.Same(page.Rows[1], RovingFocus.Target(page.List, play, Key.Down));
        page.Window.Close();
    });

    [Fact]
    public void Enter_OnAFocusedRow_PlaysFromIt() => Headless.Run(() =>
    {
        var page = Open();
        page.Rows[1].Focus(NavigationMethod.Tab);

        page.Rows[1].RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = page.Rows[1],
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(["a", "b"], page.Queue.Items.Select(t => t.VideoId));
        Assert.Equal("b", page.Queue.Current?.VideoId);
        page.Window.Close();
    });

    [Fact]
    public void TheMenuKeyOnAFocusedRow_OpensItsTrackMenu() => Headless.Run(() =>
    {
        TrackMenu.Actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, new ToastHostViewModel());
        var page = Open();
        var row = page.Rows[1];
        row.Focus(NavigationMethod.Tab);

        row.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = row });

        var flyout = Assert.IsType<MenuFlyout>(row.ContextFlyout);
        Assert.True(flyout.IsOpen);
        Assert.Equal("b", ((MenuItem)flyout.Items[0]!).CommandParameter is TrackInfo t ? t.VideoId : null);
        flyout.Hide();
        page.Window.Close();
    });

    [Fact]
    public void Enter_OnARowButton_IsTheButtonsOwn() => Headless.Run(() =>
    {
        var page = Open();
        page.Rows[1].Focus(NavigationMethod.Tab);
        Settle(page.Window);
        var add = ShownActions(page.Rows[1]).Single(b => AutomationProperties.GetName(b) == "Add to queue");
        add.Command = null; // the test shell has no track actions behind it

        add.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = add });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Not "play from here" as well: the row didn't start playing.
        Assert.Empty(page.Queue.Items);
        page.Window.Close();
    });
}
