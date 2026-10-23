using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>Reduced motion, accessible names and keyboard access.</summary>
public class AccessibilityTests
{
    private static TrackInfo Track(string id, string title = "Instant Crush") => new()
    {
        VideoId = id,
        Title = title,
        Artists = [new("Daft Punk", "UCdp"), new("Julian Casablancas")],
    };

    [Theory]
    [InlineData(MotionPreference.System, false, false)]
    [InlineData(MotionPreference.System, true, true)]
    [InlineData(MotionPreference.Reduced, false, true)]
    [InlineData(MotionPreference.Reduced, true, true)]
    [InlineData(MotionPreference.Full, true, false)]
    [InlineData(MotionPreference.Full, false, false)]
    public void ReduceMotion_FollowsTheSystemUnlessOverridden(MotionPreference preference, bool system, bool expected)
    {
        var appearance = new AppearanceSettings { Motion = preference, SystemReducesMotion = system };
        Assert.Equal(expected, appearance.ReduceMotion);
    }

    [Fact]
    public void ReduceMotion_IsAnnouncedWhenEitherInputChanges()
    {
        var appearance = new AppearanceSettings();
        var raised = 0;
        appearance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppearanceSettings.ReduceMotion))
                raised++;
        };

        appearance.SystemReducesMotion = true;
        appearance.Motion = MotionPreference.Full;

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Durations_AreZeroUnderReducedMotion()
    {
        Assert.Equal(TimeSpan.Zero, AppearanceSettings.Duration(TimeSpan.FromMilliseconds(400), reduceMotion: true));
        Assert.Equal(TimeSpan.FromMilliseconds(400), AppearanceSettings.Duration(TimeSpan.FromMilliseconds(400), reduceMotion: false));
    }

    [Theory]
    [InlineData("system", MotionPreference.System)]
    [InlineData("reduced", MotionPreference.Reduced)]
    [InlineData("full", MotionPreference.Full)]
    [InlineData("sideways", MotionPreference.System)]
    [InlineData(null, MotionPreference.System)]
    public void MotionSetting_ParsesAndRoundTrips(string? stored, MotionPreference expected)
    {
        Assert.Equal(expected, AppearanceSettings.ParseMotion(stored));
        Assert.Equal(expected, AppearanceSettings.ParseMotion(AppearanceSettings.FormatMotion(expected)));
    }

    [Fact]
    public void MotionSetting_DefaultsToFollowingTheSystem() =>
        Assert.Equal("system", new AppSettings().Motion);

    [Theory]
    [InlineData("false\n", true)]
    [InlineData("true\n", false)]
    [InlineData("", null)]
    [InlineData("No such schema “org.gnome.desktop.interface”", null)]
    public void GnomeSetting_AnimationsOffMeansReduce(string output, bool? expected) =>
        Assert.Equal(expected, SystemMotion.ParseGnome(output));

    [Fact]
    public void SettingsPage_MotionChoice_ReachesTheSharedSettingAndSaves()
    {
        var appearance = new AppearanceSettings();
        var saves = 0;
        var page = new SettingsPageViewModel(
            new Rambutan.Core.Settings.PlaybackSettings(), new AppSettings(), appearance,
            new Rambutan.Integrations.UpdateService(null), new SessionViewModel(), () => saves++);

        page.MotionIndex = (int)MotionPreference.Reduced;

        Assert.Equal(MotionPreference.Reduced, appearance.Motion);
        Assert.True(appearance.ReduceMotion);
        Assert.Equal(1, saves);

        appearance.SystemReducesMotion = true;
        Assert.StartsWith("Your system asks for reduced motion", page.SystemMotionNote);
    }

    [Fact]
    public void Backdrop_FadesOverFourHundredMs_OrNotAtAll() => Headless.Run(() =>
    {
        var background = new PageBackground();
        Assert.Equal(PageBackground.FadeDuration, background.CurrentFade);

        background.ReduceMotion = true;
        Assert.Equal(TimeSpan.Zero, background.CurrentFade);

        background.ReduceMotion = false;
        Assert.Equal(TimeSpan.FromMilliseconds(400), background.CurrentFade);
    });

    [Fact]
    public void ToastEntrance_IsAShortRise_OrNothing() => Headless.Run(() =>
    {
        Assert.Null(ToastHost.EntranceTransitions(reduceMotion: true));

        var transitions = ToastHost.EntranceTransitions(reduceMotion: false);
        Assert.NotNull(transitions);
        Assert.Equal(2, transitions.Count);
        Assert.All(transitions, t => Assert.Equal(ToastHost.EntranceDuration, ((Avalonia.Animation.ITransition)t) switch
        {
            Avalonia.Animation.DoubleTransition d => d.Duration,
            Avalonia.Animation.TransformOperationsTransition o => o.Duration,
            _ => TimeSpan.MaxValue,
        }));
    });

    [Fact]
    public void AToast_UnderReducedMotion_IsDrawnInPlaceAtOnce() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var toasts = new ToastHostViewModel(new ManualClock().Schedule);
        var host = new ToastHost { DataContext = toasts, ReduceMotion = true };
        var window = new Window { Width = 800, Height = 400, Content = host };
        window.Show();

        toasts.Show("Added “Aerodynamic” to the queue");
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var toast = host.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("toast"));
        Assert.Equal(1, toast.Opacity);
        Assert.Null(toast.Transitions);
        Assert.Equal("Added “Aerodynamic” to the queue", AutomationProperties.GetName(toast));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(toast));

        toasts.Show("Couldn't play “Discovery”", ToastKind.Error);
        window.UpdateLayout();
        var error = host.GetVisualDescendants().OfType<Border>().Last(b => b.Classes.Contains("toast"));
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(error));
        window.Close();
    });

    [Fact]
    public void AToast_WithMotion_RisesIntoPlace() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var toasts = new ToastHostViewModel(new ManualClock().Schedule);
        var host = new ToastHost { DataContext = toasts, ReduceMotion = false };
        var window = new Window { Width = 800, Height = 400, Content = host };
        window.Show();

        toasts.Show("Liked “Instant Crush”");
        window.UpdateLayout();

        var toast = host.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("toast"));
        Assert.NotNull(toast.Transitions);
        Assert.Equal(2, toast.Transitions!.Count);
        window.Close();
    });

    private static Button IconButton(string? tip)
    {
        var button = new Button { Content = new PathIcon() };
        if (tip is not null)
            ToolTip.SetTip(button, tip);
        return button;
    }

    [Theory]
    [InlineData("Next (Ctrl+→)", "Next", "Ctrl+→")]
    [InlineData("Save to playlist", "Save to playlist", null)]
    [InlineData("Back (Alt+←)", "Back", "Alt+←")]
    [InlineData("Shuffle: on", "Shuffle: on", null)]
    public void Tips_SplitIntoNameAndShortcut(string tip, string name, string? shortcut) =>
        Assert.Equal((name, shortcut), AccessibleNames.SplitTip(tip));

    [Fact]
    public void AnIconButton_IsNamedByItsTooltip_AndRenamedWithIt() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        var button = IconButton("Next (Ctrl+→)");

        Assert.Equal("Next", AutomationProperties.GetName(button));
        Assert.Equal("Ctrl+→", AutomationProperties.GetAcceleratorKey(button));

        ToolTip.SetTip(button, "Pause (Space)");
        Assert.Equal("Pause", AutomationProperties.GetName(button));
    });

    [Fact]
    public void AnExplicitName_IsNeverReplaced() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        var button = new Button { Content = new PathIcon() };
        AutomationProperties.SetName(button, "Play Discovery");

        ToolTip.SetTip(button, "Play");

        Assert.Equal("Play Discovery", AutomationProperties.GetName(button));
    });

    [Fact]
    public void ABoundName_WinsOverTheTip_EvenWhenItsValueArrivesLater() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        // XAML order: the tip is set, then the name's binding, before the data arrives.
        var button = new Button { Content = new PathIcon() };
        ToolTip.SetTip(button, "Play");
        button.Bind(AutomationProperties.NameProperty,
            new Avalonia.Data.Binding(nameof(CatalogEntry.Title)) { StringFormat = "Play {0}" });

        button.DataContext = new PlaylistEntry { Title = "Mellow Pop Classics", PlaylistId = "PL1" };
        Assert.Equal("Play Mellow Pop Classics", AutomationProperties.GetName(button));

        // A tip change later doesn't take the name back either.
        ToolTip.SetTip(button, "Play now");
        Assert.Equal("Play Mellow Pop Classics", AutomationProperties.GetName(button));
    });

    [Fact]
    public void ACardsPlayButton_IsNamedForWhatItPlays() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var card = new EntryCard
        {
            DataContext = new PlaylistEntry { Title = "Mellow Pop Classics", Subtitle = "Playlist", PlaylistId = "PL1" },
        };
        var window = new Window { Width = 300, Height = 300, Content = card };
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var play = card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("cardplay"));
        Assert.Equal("Play Mellow Pop Classics",
            Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(play).GetName());
        window.Close();
    });

    [Fact]
    public void ATextButton_KeepsItsText_EvenWhenItsTipCameFirst() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        // XAML order: attributes (the tip) before content.
        var link = new Button();
        ToolTip.SetTip(link, "Go to Random Access Memories");
        link.Content = new TextBlock { Text = "Instant Crush" };

        Assert.True(string.IsNullOrEmpty(AutomationProperties.GetName(link)));
    });

    [Fact]
    public void AnIconAndLabelButton_IsNamedByItsLabel() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { new PathIcon(), new TextBlock { Text = "Play all" } },
            },
        };
        var window = new Window { Content = button };
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("Play all", AutomationProperties.GetName(button));
        window.Close();
    });

    [Fact]
    public void A_downloaded_track_row_says_so_in_its_name()
    {
        // An id no other test uses: the hook is process-wide.
        const string downloaded = "dl-name-test";
        var previous = AccessibleNames.IsDownloaded;
        AccessibleNames.IsDownloaded = id => id == downloaded;
        try
        {
            Assert.Equal("Instant Crush, Daft Punk, Julian Casablancas, downloaded",
                AccessibleNames.RowName(Track(downloaded)));
            Assert.Equal("Instant Crush, Daft Punk, Julian Casablancas, downloaded",
                AccessibleNames.RowName(new SongEntry { Title = "Instant Crush", Track = Track(downloaded) }));
            Assert.EndsWith(", downloaded", AccessibleNames.RowName(new QueueItemViewModel(Track(downloaded))));
            Assert.Equal("Instant Crush, Daft Punk, Julian Casablancas",
                AccessibleNames.RowName(Track("dl-name-other")));
            // The plain name, which the Downloads page builds on, stays plain.
            Assert.Equal("Instant Crush, Daft Punk, Julian Casablancas", AccessibleNames.For(Track(downloaded)));
        }
        finally
        {
            AccessibleNames.IsDownloaded = previous;
        }
    }

    [Fact]
    public void Items_HaveSpokenNames()
    {
        Assert.Equal("Instant Crush, Daft Punk, Julian Casablancas", AccessibleNames.For(Track("a")));
        Assert.Equal("Discovery, Album • Daft Punk",
            AccessibleNames.For(new AlbumEntry { Title = "Discovery", Subtitle = "Album • Daft Punk", BrowseId = "b" }));
        Assert.Equal("Daft Punk", AccessibleNames.For(new ArtistEntry { Title = "Daft Punk", BrowseId = "c" }));
        Assert.Null(AccessibleNames.For("not an item"));
    }

    [Fact]
    public void QueueRows_SayWhichIsPlaying_AndFollowTheQueue() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        var queue = new QueueModel();
        queue.SetQueue([Track("a", "One"), Track("b", "Two")]);
        var vm = new QueueViewModel(queue, null);
        var list = new ListBox { ItemsSource = vm.Items };
        var window = new Window { Width = 400, Height = 300, Content = list };
        window.Show();
        window.UpdateLayout();

        var first = list.ContainerFromIndex(0)!;
        var second = list.ContainerFromIndex(1)!;
        // Nothing has played yet (a restored queue): the row says what its caption says.
        Assert.Equal("One, Daft Punk, Julian Casablancas, where you left off", AutomationProperties.GetName(first));
        Assert.Equal("Two, Daft Punk, Julian Casablancas", AutomationProperties.GetName(second));

        queue.NextTrack(TrackEndReason.Manual);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.EndsWith(", played", AutomationProperties.GetName(first));
        Assert.EndsWith(", where you left off", AutomationProperties.GetName(second));
        window.Close();
    });

    [Fact]
    public void ARestoredQueueRow_IsNotNowPlaying_UntilPlaybackStarts() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        var queue = new QueueModel();
        queue.SetQueue([Track("a", "One"), Track("b", "Two")], 1);
        var coordinator = FakePlayback.Coordinator(queue);
        var vm = new QueueViewModel(queue, coordinator);
        var list = new ListBox { ItemsSource = vm.Items };
        var window = new Window { Width = 400, Height = 300, Content = list };
        window.Show();
        window.UpdateLayout();

        var current = list.ContainerFromIndex(1)!;
        Assert.Equal("Where you left off", vm.Items[1].Section);
        Assert.Equal("Two, Daft Punk, Julian Casablancas, where you left off", AutomationProperties.GetName(current));

        coordinator.JumpToAsync(1).GetAwaiter().GetResult();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("Now playing", vm.Items[1].Section);
        Assert.Equal("Two, Daft Punk, Julian Casablancas, now playing", AutomationProperties.GetName(current));
        Assert.Equal("One, Daft Punk, Julian Casablancas, played", AutomationProperties.GetName(list.ContainerFromIndex(0)!));
        window.Close();
    });

    private static (Window Window, ItemsControl Items, Button[] Buttons) Row(int count)
    {
        var buttons = Enumerable.Range(0, count)
            .Select(i => new Button { Content = $"b{i}", Width = 60, Height = 30 })
            .ToArray();
        var items = new ItemsControl
        {
            ItemsSource = buttons,
            ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal }),
        };
        RovingFocus.SetIsEnabled(items, true);
        var before = new Button { Content = "before" };
        var after = new Button { Content = "after" };
        var window = new Window
        {
            Width = 600, Height = 200,
            Content = new StackPanel { Children = { before, items, after } },
        };
        window.Show();
        window.UpdateLayout();
        return (window, items, buttons);
    }

    [Fact]
    public void ACollection_IsOneTabStop() => Headless.Run(() =>
    {
        var (window, items, buttons) = Row(5);
        var before = (Button)((StackPanel)window.Content!).Children[0];
        var after = (Button)((StackPanel)window.Content!).Children[2];

        Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(items));

        before.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.True(buttons[0].IsFocused);
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.True(after.IsFocused); // five buttons, one stop

        // Back in, it returns to the one last used.
        buttons[3].Focus(NavigationMethod.Directional);
        after.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Assert.True(buttons[3].IsFocused);
        window.Close();
    });

    [Fact]
    public void Arrows_MoveWithinTheCollection() => Headless.Run(() =>
    {
        var (window, items, buttons) = Row(3);

        Assert.Same(buttons[1], RovingFocus.Target(items, buttons[0], Key.Right));
        Assert.Same(buttons[0], RovingFocus.Target(items, buttons[1], Key.Left));
        Assert.Null(RovingFocus.Target(items, buttons[2], Key.Right)); // nothing further
        Assert.Null(RovingFocus.Target(items, buttons[0], Key.Down));  // one row: ↓ scrolls the page
        Assert.Same(buttons[2], RovingFocus.Target(items, buttons[0], Key.End));
        Assert.Same(buttons[0], RovingFocus.Target(items, buttons[2], Key.Home));

        buttons[0].Focus();
        buttons[0].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right, Source = buttons[0] });
        Assert.True(buttons[1].IsFocused);
        window.Close();
    });

    [Fact]
    public void Arrows_SkipHiddenAndDisabledStops() => Headless.Run(() =>
    {
        var (window, items, buttons) = Row(4);
        buttons[1].IsVisible = false;
        buttons[2].IsEnabled = false;
        window.UpdateLayout();

        Assert.Same(buttons[3], RovingFocus.Target(items, buttons[0], Key.Right));
        window.Close();
    });

    [Fact]
    public void TheMenuKeyOnARow_OpensTheTrackMenu() => Headless.Run(() =>
    {
        TrackMenu.Actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, new ToastHostViewModel());
        var list = new ListBox
        {
            ItemsSource = new[] { Track("a"), Track("b") },
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<TrackInfo>((_, _) =>
            {
                var row = new Border { Height = 40, Background = Brushes.Transparent };
                row.Bind(TrackMenu.TrackProperty, new Avalonia.Data.Binding());
                return row;
            }),
        };
        var window = new Window { Width = 400, Height = 300, Content = list };
        window.Show();
        window.UpdateLayout();

        // Shift+F10 / the menu key raise the request on the focused row, the
        // ListBoxItem, which sits above the element that owns the menu.
        var container = (ListBoxItem)list.ContainerFromIndex(1)!;
        container.Focus();
        container.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = container });

        var row = container.GetVisualDescendants().OfType<Border>().First(b => TrackMenu.GetTrack(b) is not null);
        var flyout = Assert.IsType<MenuFlyout>(row.ContextFlyout);
        Assert.True(flyout.IsOpen);
        Assert.Equal("b", ((MenuItem)flyout.Items[0]!).CommandParameter is TrackInfo t ? t.VideoId : null);
        flyout.Hide();

        // Shift+F10, which X11 doesn't turn into a context request by itself.
        // It opens on the release: on Windows the release would otherwise enter
        // menu mode and dismiss a menu opened on the press.
        window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
        Assert.False(flyout.IsOpen);
        window.KeyRelease(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
        Assert.True(flyout.IsOpen);
        flyout.Hide();
        window.Close();
    });

    [Fact]
    public void ACard_OpensItsMenuFromTheKeyboard_AndARecycledCardChangesMenus() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, new ToastHostViewModel());
        TrackMenu.Actions = actions;
        CollectionMenu.Actions = new CollectionActionsViewModel(null!, null!, actions);
        try
        {
            var card = new EntryCard { DataContext = new SongEntry { Title = "Instant Crush", Track = Track("a") } };
            var window = new Window { Width = 300, Height = 300, Content = card };
            window.Show();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var host = card.GetVisualDescendants().OfType<Panel>().Single(p => p.Classes.Contains("cardhost"));
            var flyout = Assert.IsType<MenuFlyout>(host.ContextFlyout);
            var button = card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("card"));
            string[] Headers() => flyout.Items.OfType<MenuItem>().Select(i => (string)i.Header!).ToArray();

            void MenuKey()
            {
                button.Focus();
                button.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = button });
            }

            // The menu key raises the request on the focused card, inside the
            // element that owns the menu, as does a right-click.
            MenuKey();
            Assert.True(flyout.IsOpen);
            Assert.Equal("a", ((MenuItem)flyout.Items[0]!).CommandParameter is TrackInfo t ? t.VideoId : null);
            flyout.Hide();

            // Shift+F10 opens it on the release, as on a row.
            window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
            Assert.False(flyout.IsOpen);
            window.KeyRelease(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
            Assert.True(flyout.IsOpen);
            flyout.Hide();

            // The same card showing an album (a recycled container) has the album's menu.
            card.DataContext = new AlbumEntry { Title = "Random Access Memories", BrowseId = "MPREb1" };
            window.UpdateLayout();
            MenuKey();
            Assert.True(flyout.IsOpen);
            Assert.Equal(["Play", "Shuffle", "Play next", "Add to queue", "Go to artist", "Share"], Headers());
            flyout.Hide();
            window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
            window.KeyRelease(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
            Assert.True(flyout.IsOpen);
            flyout.Hide();

            // And back to a song.
            card.DataContext = new SongEntry { Title = "Digital Love", Track = Track("b") };
            window.UpdateLayout();
            MenuKey();
            Assert.Equal("Play next", Headers()[0]);
            flyout.Hide();

            // With nothing to show, nothing opens: no empty menu.
            card.DataContext = null;
            window.UpdateLayout();
            MenuKey();
            Assert.False(flyout.IsOpen);
            window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
            window.KeyRelease(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
            Assert.False(flyout.IsOpen);
            window.Close();
        }
        finally
        {
            CollectionMenu.Actions = null;
        }
    });

    [Fact]
    public void ACardWhoseAlbumLoads_KeepsItsPlayButtonUp_AndSaysSo() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var catalog = new FakeCatalog
        {
            Album = new Rambutan.Core.Models.AlbumPage { BrowseId = "MPREb1", Title = "Discovery", Tracks = [Track("a")] },
        };
        var gate = new TaskCompletionSource();
        catalog.AlbumGate = gate.Task;
        var queue = new QueueModel();
        var actions = new TrackActionsViewModel(catalog, queue, null!, null!, new ToastHostViewModel());
        var collections = new CollectionActionsViewModel(catalog, FakePlayback.Coordinator(queue), actions);
        CollectionMenu.Actions = collections;
        try
        {
            var entry = new AlbumEntry { Title = "Discovery", BrowseId = "MPREb1" };
            var card = new EntryCard { DataContext = entry };
            var window = new Window { Width = 300, Height = 300, Content = card };
            window.Show();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var play = card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("cardplay"));
            var spinner = ((Panel)play.Content!).Children.OfType<Avalonia.Controls.Shapes.Arc>().Single();
            string? Name() => Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(play).GetName();

            // At rest: hidden until hover or focus, the glyph and not the spinner.
            Assert.False(play.IsVisible);
            Assert.False(spinner.IsVisible);
            Assert.Equal("Play Discovery", Name());

            // Started from the card's menu (an equal entry of its own).
            var loading = collections.PlayCommand.ExecuteAsync(entry with { });
            window.UpdateLayout();
            Assert.True(play.IsVisible);
            Assert.True(spinner.IsVisible);
            Assert.Equal("Loading Discovery", Name());
            // Drawn on the accent, so in the colour made for that, not the
            // player bar's dark ring.
            Assert.True(card.TryFindResource("Token.Brush.OnAccent", out var onAccent));
            Assert.Same(onAccent, spinner.Stroke);

            gate.SetResult();
            loading.GetAwaiter().GetResult();
            window.UpdateLayout();
            Assert.False(play.IsVisible);
            Assert.False(spinner.IsVisible);
            Assert.Equal("Play Discovery", Name());
            window.Close();
        }
        finally
        {
            CollectionMenu.Actions = null;
        }
    });

    [Fact]
    public void ACardInAListMarkedInTheLibrary_OffersToRemoveIt() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, new ToastHostViewModel());
        CollectionMenu.Actions = new CollectionActionsViewModel(null!, null!, actions) { IsSignedIn = () => true };
        try
        {
            // As the Library's tabs list their cards.
            var list = new ItemsControl
            {
                ItemsSource = new CatalogEntry[] { new ArtistEntry { Title = "Daft Punk", BrowseId = "UC1" } },
                ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<CatalogEntry>((_, _) => new EntryCard()),
            };
            var window = new Window { Width = 300, Height = 300, Content = list };
            window.Show();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var host = list.GetVisualDescendants().OfType<Panel>().Single(p => p.Classes.Contains("cardhost"));
            var flyout = Assert.IsType<MenuFlyout>(host.ContextFlyout);
            var button = list.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("card"));
            string[] Headers()
            {
                button.Focus();
                button.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = button });
                var headers = flyout.Items.OfType<MenuItem>().Select(i => (string)i.Header!).ToArray();
                flyout.Hide();
                return headers;
            }

            Assert.Contains("Subscribe", Headers());

            CollectionMenu.SetInLibrary(list, true);
            Assert.Contains("Unsubscribe", Headers());
            Assert.DoesNotContain("Subscribe", Headers());
            window.Close();
        }
        finally
        {
            CollectionMenu.Actions = null;
        }
    });

    [Fact]
    public void AnArtistCard_ShowsTheButtonOnlyWhileItsTopSongsLoad() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var catalog = new FakeCatalog
        {
            Artist = new Rambutan.Core.Models.ArtistPage
            {
                BrowseId = "UC1", Name = "Daft Punk", TopSongs = [Track("a")], Shelves = [],
            },
        };
        var gate = new TaskCompletionSource();
        catalog.ArtistGate = gate.Task;
        var queue = new QueueModel();
        var actions = new TrackActionsViewModel(catalog, queue, null!, null!, new ToastHostViewModel());
        var collections = new CollectionActionsViewModel(catalog, FakePlayback.Coordinator(queue), actions);
        CollectionMenu.Actions = collections;
        try
        {
            var entry = new ArtistEntry { Title = "Daft Punk", BrowseId = "UC1" };
            var card = new EntryCard { DataContext = entry };
            var window = new Window { Width = 300, Height = 300, Content = card };
            window.Show();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var play = card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("cardplay"));
            string? Name() => Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(play).GetName();

            // An artist has no single thing to play, so no button at rest, even on focus.
            card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("card")).Focus();
            window.UpdateLayout();
            Assert.False(play.IsEffectivelyVisible);

            // "Play top songs" from the card's menu.
            var loading = collections.PlayCommand.ExecuteAsync(entry);
            window.UpdateLayout();
            Assert.True(play.IsEffectivelyVisible);
            Assert.Equal("Loading Daft Punk", Name());

            gate.SetResult();
            loading.GetAwaiter().GetResult();
            window.UpdateLayout();
            Assert.False(play.IsEffectivelyVisible);
            Assert.Equal("a", queue.Current?.VideoId);
            window.Close();
        }
        finally
        {
            CollectionMenu.Actions = null;
        }
    });

    [Fact]
    public void EveryButtonInTheShell_HasASpokenName() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var window = new MainWindow { DataContext = new MainViewModel() };
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Empty(Unnamed(window));

        // The transport in particular, by what it does now.
        var names = window.GetVisualDescendants().OfType<Button>()
            .Select(AutomationProperties.GetName).ToHashSet();
        Assert.Contains("Previous", names);
        Assert.Contains("Play", names);
        Assert.Contains("Next", names);
        Assert.Contains("Repeat: off", names);
        Assert.Contains("Shuffle: off", names);
        Assert.Contains("Mute", names);
        Assert.Contains("Home", names);
        window.Close();
    });

    public static TheoryData<string> StatusRegions => ["NetworkWait", "ReadingFiles"];

    [Theory]
    [MemberData(nameof(StatusRegions))]
    public void AWaitForTheNetwork_OrFilesBeingRead_IsOnTheStatusStrip_AsAPoliteLiveRegion(string region) => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        var strip = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "StatusStrip");
        Assert.False(vm.HasStatus);
        Assert.Equal(0, strip.Bounds.Height);
        Assert.Equal(default, strip.Margin);

        // The region is in the tree, empty, before the wait starts: Avalonia
        // announces a live region when an existing element's Name changes.
        var text = strip.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == region);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(text));
        Assert.True(text.IsEffectivelyVisible);
        var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(text);
        var named = new List<object?>();
        peer.PropertyChanged += (_, e) =>
        {
            if (e.Property == AutomationElementIdentifiers.NameProperty)
                named.Add(e.NewValue);
        };

        if (region == "NetworkWait")
            vm.TrackWaitingForNetwork = new TrackInfo { VideoId = "a", Title = "Aerodynamic", Artists = [] };
        else
            vm.ReadingFiles = 120;
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(vm.HasStatus);
        Assert.True(strip.Bounds.Height > 0);
        Assert.Contains(region == "NetworkWait" ? "Aerodynamic" : "Reading 120 files…", text.Text);
        Assert.Equal(text.Text, peer.GetName());
        Assert.Equal(AutomationLiveSetting.Polite, peer.GetLiveSetting());
        Assert.Equal([text.Text], named);

        vm.TrackWaitingForNetwork = null;
        vm.ReadingFiles = null;
        window.UpdateLayout();
        Assert.False(vm.HasStatus);
        Assert.Equal(0, strip.Bounds.Height);
        window.Close();
    });

    [Fact]
    public void TheSidebarRail_HidesItsLabels_AndKeepsEveryButtonNamed() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var vm = new MainViewModel { IsSidebarOpen = false };
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Collapsed is a rail of icons, not gone.
        var sidebar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Sidebar");
        Assert.True(sidebar.IsEffectivelyVisible);
        Assert.Equal(68, sidebar.Bounds.Width);
        var home = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("nav") && AutomationProperties.GetName(b) == "Home");
        Assert.DoesNotContain(home.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible);
        Assert.True(ToolTip.GetServiceEnabled(home));
        Assert.Empty(Unnamed(window));

        // The tooltip's text is a TextBlock whose logical parent is the button,
        // so a rail style that hides the label must not reach it too.
        ToolTip.SetIsOpen(home, true);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var tip = OpenToolTip(home);
        tip.UpdateLayout();
        var tipText = Assert.Single(tip.GetVisualDescendants().OfType<TextBlock>());
        Assert.Equal("Home", tipText.Text);
        Assert.True(tipText.IsEffectivelyVisible);
        ToolTip.SetIsOpen(home, false);

        vm.ToggleSidebarCommand.Execute(null);
        window.UpdateLayout();
        Assert.Equal(220, sidebar.Bounds.Width);
        Assert.False(ToolTip.GetServiceEnabled(home));
        window.Close();
    });

    [Fact]
    public void EveryInputOnTheSettingsPage_HasASpokenName() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var page = new SettingsPageViewModel(
            new Rambutan.Core.Settings.PlaybackSettings { CrossfadeEnabled = true }, new AppSettings(),
            new AppearanceSettings(), new Rambutan.Integrations.UpdateService(null), new SessionViewModel(), () => { });
        var view = new SettingsPage { DataContext = page };
        var window = new Window { Width = 900, Height = 900, Content = view };
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // The name UI Automation reports, which falls back to LabeledBy.
        static string? Spoken(Control c) =>
            Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(c).GetName();

        var inputs = view.GetVisualDescendants()
            .Where(v => v is Slider or ToggleSwitch or ComboBox or ListBox)
            .Cast<Control>()
            .Where(c => c.IsEffectivelyVisible && c.TemplatedParent is null)
            .ToArray();
        Assert.Contains(inputs, c => c is Slider);
        Assert.Empty(inputs.Where(c => string.IsNullOrWhiteSpace(Spoken(c)))
            .Select(c => $"{c.GetType().Name} {c.Name}"));

        var sliders = inputs.OfType<Slider>().Select(s => Spoken(s) ?? "").ToArray();
        Assert.Equal(["Crossfade duration", "Audio disk cache"], sliders);
        Assert.Contains("Motion", inputs.OfType<ComboBox>().Select(Spoken));
        window.Close();
    });

    [Fact]
    public void Tab_ReachesThePageBeforeTheQueue_AndThePlayerBarLast() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var vm = new MainViewModel { IsQueueOpen = true }; // its design-time queue has rows
        vm.Navigation.GoTo(new SettingsPageViewModel(), NavSection.Settings);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
        var home = buttons.First(b => AutomationProperties.GetName(b) == "Home");
        var page = window.GetVisualDescendants().OfType<SettingsPage>().Single();
        var queue = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "QueueList");
        var repeat = buttons.First(b => AutomationProperties.GetName(b) == "Repeat: off");

        // One lap of Tab from Home, as the Windows check recorded it.
        home.Focus(NavigationMethod.Tab);
        var lap = new List<Visual>();
        for (var i = 0; i < 60; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            var focused = (Visual)window.FocusManager!.GetFocusedElement()!;
            if (ReferenceEquals(focused, home))
                break;
            lap.Add(focused);
        }

        int Last(Visual within) => lap.FindLastIndex(v => within.IsVisualAncestorOf(v));
        int First(Visual within) => lap.FindIndex(v => within.IsVisualAncestorOf(v));
        Assert.Equal(["Search", "Library", "Downloads", "Settings"],
            lap.Take(4).Select(v => AutomationProperties.GetName((Control)v)));
        Assert.Equal(4, First(page));
        Assert.True(Last(page) < First(queue), "the queue came before the end of the page");
        Assert.True(Last(queue) < lap.IndexOf(repeat), "the player bar came before the queue");

        // Shift+Tab walks the same lap backwards: from the page's first control
        // back into the sidebar, not into the queue.
        ((IInputElement)lap[4]).Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Assert.Equal("Settings", AutomationProperties.GetName((Control)window.FocusManager!.GetFocusedElement()!));
        window.Close();
    });

    /// <summary>Visible buttons a screen reader would announce as just "button".
    /// Template parts (a slider's step buttons) are the control's own business.</summary>
    private static string[] Unnamed(Visual root) =>
        root.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.TemplatedParent is null)
            .Where(b => string.IsNullOrEmpty(AutomationProperties.GetName(b)) && b.Content is not (string or TextBlock))
            .Select(b => $"{b.Name ?? "?"} [{string.Join(",", b.Classes)}]")
            .ToArray();

    /// <summary>The ToolTip control Avalonia made for a string tip. It lives in a
    /// popup outside the window's visual tree and is reachable only through the
    /// internal attached property that holds it.</summary>
    private static ToolTip OpenToolTip(Control owner)
    {
        var property = (AvaloniaProperty)typeof(ToolTip)
            .GetField("ToolTipProperty", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(null)!;
        return Assert.IsType<ToolTip>(owner.GetValue(property));
    }

    /// <summary>Controls a screen reader reaches but can't name, read from the
    /// automation tree itself, so template parts count too.</summary>
    private static string[] Silent(Control root)
    {
        var found = new List<string>();
        Walk(Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(root));
        return found.ToArray();

        void Walk(Avalonia.Automation.Peers.AutomationPeer peer)
        {
            foreach (var child in peer.GetChildren())
            {
                if (child.IsControlElement() && Spoken.Contains(child.GetAutomationControlType()) &&
                    string.IsNullOrEmpty(child.GetName()))
                    found.Add($"{child.GetClassName()} in {peer.GetClassName()} \"{peer.GetName()}\"");
                Walk(child);
            }
        }
    }

    private static readonly HashSet<Avalonia.Automation.Peers.AutomationControlType> Spoken =
    [
        Avalonia.Automation.Peers.AutomationControlType.Button,
        Avalonia.Automation.Peers.AutomationControlType.Thumb,
        Avalonia.Automation.Peers.AutomationControlType.Slider,
        Avalonia.Automation.Peers.AutomationControlType.ProgressBar,
        Avalonia.Automation.Peers.AutomationControlType.CheckBox,
        Avalonia.Automation.Peers.AutomationControlType.ComboBox,
    ];

    [Fact]
    public void TheShell_HasNoSilentControls_SliderPartsIncluded() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var window = new MainWindow { DataContext = new MainViewModel() };
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Empty(Silent(window));
        window.Close();
    });

    [Fact]
    public void TheSettingsPage_HasNoSilentControls_SliderPartsIncluded() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var page = new SettingsPageViewModel(
            new Rambutan.Core.Settings.PlaybackSettings { CrossfadeEnabled = true }, new AppSettings(),
            new AppearanceSettings(), new Rambutan.Integrations.UpdateService(null), new SessionViewModel(), () => { });
        var view = new SettingsPage { DataContext = page };
        var window = new Window { Width = 900, Height = 2400, Content = view };
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(view.GetVisualDescendants().OfType<Slider>());
        Assert.Empty(Silent(view));
        window.Close();
    });

    [Fact]
    public void AnAlbumPage_NamesItsRowsAndEveryRowButton() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var catalog = new FakeCatalog
        {
            Album = new Rambutan.Core.Models.AlbumPage
            {
                BrowseId = "MPRE", Title = "Random Access Memories", Artist = "Daft Punk", ArtistBrowseId = "UC1",
                Tracks = [Track("a", "Give Life Back to Music"), Track("b", "The Game of Love")],
            },
        };
        var page = new AlbumPageViewModel(catalog, null!, "MPRE");
        page.LoadAsync().GetAwaiter().GetResult();
        var view = new Rambutan.App.Views.AlbumPage { DataContext = page };
        var window = new Window { Width = 1000, Height = 700, DataContext = new MainViewModel(), Content = view };
        window.Show();
        window.UpdateLayout();

        var list = view.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.Equal("Tracks", AutomationProperties.GetName(list));
        list.SelectedIndex = 0; // reveals the row's buttons
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("Give Life Back to Music, Daft Punk, Julian Casablancas",
            AutomationProperties.GetName(list.ContainerFromIndex(0)!));
        Assert.Empty(Unnamed(view));
        var rowNames = list.ContainerFromIndex(0)!.GetVisualDescendants().OfType<Button>()
            .Select(AutomationProperties.GetName).ToArray();
        Assert.Contains("Play from here", rowNames);
        Assert.Contains("Add to queue", rowNames);
        Assert.Contains("Like", rowNames);
        window.Close();
    });
}

/// <summary>The app's tokens and styles, for tests that build real views (the
/// test app is the bare Fluent theme). The tokens are App.axaml's own file.</summary>
internal static class ShellResources
{
    // Per application: the headless session may start a fresh one per test.
    private static Application? _loadedInto;

    public static void Ensure()
    {
        var app = Application.Current!;
        if (ReferenceEquals(_loadedInto, app))
            return;
        _loadedInto = app;

        foreach (var dictionary in new[] { "Tokens", "States" })
        {
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Rambutan.App/"))
            {
                Source = new Uri($"avares://Rambutan.App/Styles/{dictionary}.axaml"),
            });
        }
        foreach (var sheet in new[] { "Icons", "Controls", "Browse" })
        {
            app.Styles.Add(new StyleInclude(new Uri("avares://Rambutan.App/"))
            {
                Source = new Uri($"avares://Rambutan.App/Styles/{sheet}.axaml"),
            });
        }
    }
}
