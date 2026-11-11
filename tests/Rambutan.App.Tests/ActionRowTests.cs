using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Tests;

public class ActionRowTests
{
    private const double Gap = 10;

    private sealed class Probe : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }

    private static Button Fixed(double width, string? name = null) =>
        new() { Width = width, Height = 30, Content = null, [AutomationProperties.NameProperty] = name! };

    /// <summary>A kept 60-wide button and four 40-wide ones, as a page's hero row.</summary>
    private sealed class Rig
    {
        public Rig(double width, bool unbounded = false)
        {
            Keep = Fixed(60, "keep");
            ActionRow.SetKeep(Keep, true);
            Moving = [Fixed(40, "a"), Fixed(40, "b"), Fixed(40, "c"), Fixed(40, "d")];
            Row = new ActionRow { Spacing = Gap };
            Row.Items.Add(Keep);
            foreach (var button in Moving)
                Row.Items.Add(button);

            if (unbounded)
            {
                Host = new StackPanel { Orientation = Orientation.Horizontal, Children = { Row } };
                Window = new Window { Width = width, Height = 200, Content = Host };
            }
            else
            {
                Host = new Border { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Child = Row };
                Window = new Window { Width = 1000, Height = 200, Content = Host };
            }

            Window.Show();
            Window.UpdateLayout();
        }

        public ActionRow Row { get; }

        public Control Host { get; }

        public Window Window { get; }

        public Button Keep { get; }

        public Button[] Moving { get; }

        public void Resize(double width)
        {
            Host.Width = width;
            Window.UpdateLayout();
        }

        public double MoreWidth => Row.MoreButton.DesiredSize.Width;

        /// <summary>A row width that keeps the pinned button, More and the first <paramref name="kept"/> movers.</summary>
        public double WidthKeeping(int kept) => 60 + (kept * (40 + Gap)) + Gap + MoreWidth + 1;
    }

    private static Control SlotOf(Control item) => (Control)item.GetVisualParent()!;

    [Fact]
    public void WideRow_PlacesEveryButtonInOrderWithoutMore() => Headless.Run(() =>
    {
        var rig = new Rig(400);

        Assert.Empty(rig.Row.Overflowed);
        Assert.False(rig.Row.ShowsMore);
        Assert.Equal(0, SlotOf(rig.Keep).Bounds.X);
        Assert.Equal(60 + Gap, SlotOf(rig.Moving[0]).Bounds.X);
        for (var i = 1; i < rig.Moving.Length; i++)
            Assert.Equal(SlotOf(rig.Moving[i - 1]).Bounds.Right + Gap, SlotOf(rig.Moving[i]).Bounds.X);
    });

    [Fact]
    public void NarrowRow_MovesTheLastButtonsIntoMore() => Headless.Run(() =>
    {
        var rig = new Rig(400);
        rig.Resize(rig.WidthKeeping(2));

        Assert.True(rig.Row.ShowsMore);
        Assert.Equal([rig.Moving[2], rig.Moving[3]], rig.Row.Overflowed);
        Assert.True(rig.Keep.IsEffectivelyVisible);
        Assert.True(rig.Moving[0].IsEffectivelyVisible);
        Assert.True(rig.Moving[1].IsEffectivelyVisible);
        Assert.True(rig.Row.MoreButton.IsEffectivelyVisible);
        foreach (var moved in rig.Row.Overflowed)
        {
            Assert.False(moved.IsEffectivelyVisible);
            Assert.True(moved.IsVisible);
        }
    });

    [Fact]
    public void RowNarrowerThanEverything_ShowsOnlyTheKeptItemAndMore() => Headless.Run(() =>
    {
        var rig = new Rig(400);
        rig.Resize(10);

        Assert.True(rig.Row.ShowsMore);
        Assert.Equal(rig.Moving, rig.Row.Overflowed);
        Assert.True(rig.Keep.IsEffectivelyVisible);
        Assert.True(rig.Row.MoreButton.IsEffectivelyVisible);
        Assert.All(rig.Moving, button => Assert.False(button.IsEffectivelyVisible));
    });

    [Fact]
    public void BuildMenu_CarriesEachMovedButtonsNameCommandAndState() => Headless.Run(() =>
    {
        var command = new Probe();
        var named = Fixed(40, "Named");
        named.Command = command;
        named.CommandParameter = "p1";
        var text = new Button { Width = 40, Content = "Text" };
        var panel = new Button { Width = 40, Content = new StackPanel { Children = { new TextBlock { Text = "Inside" } } } };
        var off = Fixed(40, "Off");
        off.IsEnabled = false;

        var row = new ActionRow { Spacing = Gap };
        var keep = Fixed(60, "keep");
        ActionRow.SetKeep(keep, true);
        row.Items.Add(keep);
        foreach (var button in new[] { named, text, panel, off })
            row.Items.Add(button);
        var window = new Window { Width = 1000, Height = 200, Content = new Border { Width = 10, HorizontalAlignment = HorizontalAlignment.Left, Child = row } };
        window.Show();
        window.UpdateLayout();

        var menu = row.BuildMenu();

        Assert.Equal(4, menu.Count);
        Assert.Equal(["Named", "Text", "Inside", "Off"], menu.Select(entry => entry.Header));
        Assert.Same(command, menu[0].Command);
        Assert.Equal("p1", menu[0].CommandParameter);
        Assert.True(menu[0].IsEnabled);
        Assert.False(menu[3].IsEnabled);
    });

    [Fact]
    public void ButtonWithIsVisibleFalse_IsNeitherPlacedNorMoved() => Headless.Run(() =>
    {
        var rig = new Rig(400);
        rig.Moving[1].IsVisible = false;
        rig.Window.UpdateLayout();

        Assert.Empty(rig.Row.Overflowed);
        Assert.False(rig.Row.ShowsMore);
        Assert.False(rig.Moving[1].IsEffectivelyVisible);
        Assert.Equal(SlotOf(rig.Moving[0]).Bounds.Right + Gap, SlotOf(rig.Moving[2]).Bounds.X);

        rig.Resize(10);
        Assert.DoesNotContain(rig.Moving[1], rig.Row.Overflowed);
        Assert.Equal(3, rig.Row.Overflowed.Count);
    });

    [Fact]
    public void MovedButtonThatBecomesInvisible_LeavesTheMenuAndMoreGoesWhenItWasTheLast() => Headless.Run(() =>
    {
        var rig = new Rig(400);
        rig.Resize(10);
        Assert.Equal(4, rig.Row.Overflowed.Count);

        foreach (var moved in rig.Row.Overflowed.ToList())
        {
            Assert.True(rig.Row.ShowsMore);
            moved.IsVisible = false;
            rig.Window.UpdateLayout();
            Assert.DoesNotContain(moved, rig.Row.Overflowed);
        }

        Assert.Empty(rig.Row.Overflowed);
        Assert.False(rig.Row.ShowsMore);
    });

    [Fact]
    public void WideningTheRow_PutsEveryButtonBackAndHidesMore() => Headless.Run(() =>
    {
        var rig = new Rig(400);
        rig.Resize(rig.WidthKeeping(1));
        Assert.True(rig.Row.ShowsMore);

        rig.Resize(400);

        Assert.Empty(rig.Row.Overflowed);
        Assert.False(rig.Row.ShowsMore);
        Assert.False(rig.Row.MoreButton.IsEffectivelyVisible);
        Assert.All(rig.Moving, button => Assert.True(button.IsEffectivelyVisible));
    });

    [Fact]
    public void Extra_TakesOnlyTheRoomLeftAndDoesNotPushButtonsIntoMore() => Headless.Run(() =>
    {
        var rig = new Rig(300);
        var extra = new Border { Width = 80 };
        rig.Row.Items.Add(extra);
        rig.Window.UpdateLayout();

        Assert.Empty(rig.Row.Overflowed);
        Assert.False(rig.Row.ShowsMore);
        Assert.True(SlotOf(extra).Bounds.Right <= rig.Row.Bounds.Width + 0.01);
        Assert.True(SlotOf(extra).Bounds.Width < 80);
    });

    [Fact]
    public void MoreButton_HasAScreenReaderName() => Headless.Run(() =>
    {
        var rig = new Rig(400);

        var name = AutomationProperties.GetName(rig.Row.MoreButton);

        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.Equal(Strings.Get("common.moreActions"), name);
        Assert.NotEqual("common.moreActions", name);
    });

    [Fact]
    public void WithMore_ItsSlotSitsAfterTheKeptAndBeforeTheMoved() => Headless.Run(() =>
    {
        var rig = new Rig(400);
        rig.Resize(rig.WidthKeeping(2));

        var slots = rig.Row.GetVisualChildren().OfType<Decorator>().ToList();
        var keep = slots.IndexOf((Decorator)SlotOf(rig.Keep));
        var more = slots.FindIndex(slot => slot.Child == rig.Row.MoreButton);
        var firstMoved = slots.IndexOf((Decorator)SlotOf(rig.Moving[2]));
        var lastKept = slots.IndexOf((Decorator)SlotOf(rig.Moving[1]));

        Assert.True(keep < more);
        Assert.True(lastKept < more);
        Assert.True(more < firstMoved);
    });

    [Fact]
    public void UnboundedWidth_MovesNothing() => Headless.Run(() =>
    {
        var rig = new Rig(50, unbounded: true);

        Assert.Empty(rig.Row.Overflowed);
        Assert.False(rig.Row.ShowsMore);
    });

    [Fact]
    public void AButtonMarkedForTheMenu_IsInItAtAnyWidth_AndTheRowStillFitsBesideMore() => Headless.Run(() =>
    {
        var rig = new Rig(400);
        var only = Fixed(40, "only");
        ActionRow.SetInMenu(only, true);
        rig.Row.Items.Add(only);
        rig.Window.UpdateLayout();

        Assert.True(rig.Row.ShowsMore);
        Assert.Equal([only], rig.Row.Overflowed);
        Assert.False(only.IsEffectivelyVisible);
        Assert.All(rig.Moving, button => Assert.True(button.IsEffectivelyVisible));
        Assert.Equal(["only"], rig.Row.BuildMenu().Select(entry => (string)entry.Header!));

        rig.Resize(rig.WidthKeeping(2));

        Assert.Equal([rig.Moving[2], rig.Moving[3], only], rig.Row.Overflowed);
    });

    [Fact]
    public void AHiddenButtonMarkedForTheMenu_ShowsNoMore() => Headless.Run(() =>
    {
        var rig = new Rig(400);
        var only = Fixed(40, "only");
        ActionRow.SetInMenu(only, true);
        only.IsVisible = false;
        rig.Row.Items.Add(only);
        rig.Window.UpdateLayout();

        Assert.False(rig.Row.ShowsMore);
        Assert.Empty(rig.Row.Overflowed);
    });

    [Fact]
    public void TheLibraryHeader_KeepsImportFromSpotifyInTheMoreMenuAndNamesTheButton() => Headless.Run(() =>
    {
        var navigation = new NavigationService(new FakeCatalog(), null!, new TrackActionsViewModel())
        {
            SpotifyImports = () => null!,
        };
        ShellResources.Ensure();
        var library = new LibraryPageViewModel(new FakeCatalog(), navigation, new TrackActionsViewModel(), null!);
        var page = new Rambutan.App.Views.LibraryPage { DataContext = library };
        var window = new Window { Width = 1200, Height = 700, Content = page };
        window.Show();
        window.UpdateLayout();
        window.UpdateLayout();

        var row = page.GetVisualDescendants().OfType<ActionRow>().First();
        Assert.True(row.ShowsMore);
        Assert.True(row.MoreButton.IsEffectivelyVisible);
        Assert.Equal(Strings.Get("common.moreActions"), AutomationProperties.GetName(row.MoreButton));
        var entry = Assert.Single(row.BuildMenu());
        Assert.Equal(Strings.Get("library.importSpotify"), entry.Header);
        Assert.Same(library.ImportFromSpotifyCommand, entry.Command);
        window.Close();
    });

    [Fact]
    public void TheLibraryHeader_HasNoMoreMenuWhereImportIsNotComposed() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var navigation = new NavigationService(new FakeCatalog(), null!, new TrackActionsViewModel());
        var library = new LibraryPageViewModel(new FakeCatalog(), navigation, new TrackActionsViewModel(), null!);
        var page = new Rambutan.App.Views.LibraryPage { DataContext = library };
        var window = new Window { Width = 1200, Height = 700, Content = page };
        window.Show();
        window.UpdateLayout();

        Assert.False(page.GetVisualDescendants().OfType<ActionRow>().First().ShowsMore);
        window.Close();
    });
}
