using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Session.SignInRequested += () => _ = ShowSignInAsync(vm.Session);
                // Coming back to the app is when a stale session gets re-checked.
                Activated += (_, _) => _ = vm.Session.OnActivatedAsync();
                vm.Actions.PlaylistPicker = async playlists =>
                    await new PlaylistPickDialog(playlists).ShowDialog<PlaylistPickResult?>(this);
                vm.Actions.TextPrompt = async (title, initial) =>
                    await new TextPromptDialog(title, initial).ShowDialog<string?>(this);
                vm.Actions.Confirm = async message =>
                    await new ConfirmDialog(message).ShowDialog<bool>(this);
                if (vm.Profiles is { } profiles)
                    profiles.ManageRequested += () => _ = ShowProfilesAsync(profiles);
                vm.NowPlaying.Lyrics.OpenEditor = editor =>
                    new LyricsEditorDialog(editor).ShowDialog(this);
                vm.Actions.CopyToClipboard = text =>
                    Clipboard?.SetTextAsync(text) ?? System.Threading.Tasks.Task.CompletedTask;

                // A new track brings its row into view. Posted: the row
                // the VM just placed isn't laid out yet in the same turn.
                vm.QueuePanel.CurrentChanged += item =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => QueueList.ScrollIntoView(item),
                        Avalonia.Threading.DispatcherPriority.Loaded);
                if (vm.QueuePanel.Items.FirstOrDefault(i => i.IsCurrent) is { } restored)
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => QueueList.ScrollIntoView(restored),
                        Avalonia.Threading.DispatcherPriority.Loaded);
                HistoryButtons.Attach(this, vm.Navigation);
            }
        };

        // Recomputed, not bound: maximizing and tiling change the decorations' edges.
        void FitShell() => Shell.Margin = ShellMargin.For(
            OffScreenMargin, WindowDecorationMargin, ExtendClientAreaTitleBarHeightHint);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == OffScreenMarginProperty || e.Property == WindowDecorationMarginProperty)
                FitShell();
            else if (e.Property == ClientSizeProperty)
            {
                HoldMinWidth();
                HoldMinHeight();
            }
        };
        FitShell();

        // Custom title bar: drag to move, double-click to toggle maximize.
        TitleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            if (e.ClickCount == 2)
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            else
                BeginMoveDrag(e);
        };

        // Seek on release; while pressed, the VM stops pushing positions. The
        // handlers live on the panel (its full height hit-tests, the slider's own
        // strip does not cover the drawn line) and the target always derives from
        // the pointer, never Slider.Value — a plain click can release before the
        // slider's own value settles.
        SeekArea.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (DataContext is MainViewModel vm && PositionSlider.IsEnabled)
            {
                vm.Player.IsSeeking = true;
                PositionSlider.Value = SeekTarget(e.GetPosition(SeekArea).X);
            }
        }, handledEventsToo: true);

        SeekArea.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (DataContext is MainViewModel { Player.IsSeeking: true } &&
                e.GetCurrentPoint(SeekArea).Properties.IsLeftButtonPressed)
            {
                PositionSlider.Value = SeekTarget(e.GetPosition(SeekArea).X);
            }
        }, handledEventsToo: true);

        // The played-range line is drawn by SeekFill, not the slider (the
        // slider template's own fill can't be kept aligned with the panel-drawn
        // track). Width follows value/duration — including thumb drags, where
        // the slider value moves while the VM holds its position.
        PositionSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty || e.Property == Slider.MaximumProperty)
                UpdateSeekFill();
        };
        SeekArea.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                UpdateSeekFill();
        };
        // The bar resized, or the identity appeared (it is hidden with nothing loaded).
        PlayerBarGrid.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                FitSeekArea();
        };
        // The compact bar's flyouts: Save opens the playlist picker, so its flyout
        // closes first; the volume flyout starts on its slider, so arrows work at once.
        BarMoreSave.Click += (_, _) => BarMoreButton.Flyout?.Hide();
        VolumeButton.Flyout!.Opened += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => VolumeFlyoutSlider.Focus(),
                Avalonia.Threading.DispatcherPriority.Loaded);
        // The window resized, the Sign in button came or went, or the text size changed.
        foreach (var part in new Control[] { Sidebar, SidebarNav, SidebarAccount })
            part.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty)
                    FitSidebar();
            };
        // Another track: a long title takes room from the seek line. Not
        // before the bar's first layout, whose parts aren't measured yet.
        BarTitle.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty && PlayerBarGrid.Bounds.Width > 0)
                FitSeekArea();
        };
        // Bounds, not IsVisible: by the time they change, the identity has been measured.
        BarIdentityActions.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                FitSeekArea();
        };

        SeekArea.AddHandler(PointerReleasedEvent, async (_, e) =>
        {
            if (DataContext is MainViewModel { Player.IsSeeking: true } vm)
            {
                var target = SeekTarget(e.GetPosition(SeekArea).X);
                vm.Player.IsSeeking = false;
                try
                {
                    await vm.Player.SeekToAsync(target);
                }
                catch
                {
                    // async void event handler: a failed seek must not crash the app.
                }
            }
        }, handledEventsToo: true);
    }

    // Widths of the parts that fold away in a compact bar, as last measured while
    // shown: a hidden control measures to nothing. The fit needs both bars' sizes
    // whichever one shows, so it can't flip back and forth on its own.
    private double _fullActionsWidth, _moreWidth, _volumeSliderWidth, _timeLabelsWidth;
    private PlayerBarFit _barFit = PlayerBarFit.Full;

    /// <summary>The window's minimum width: the compact bar with its seek line at
    /// the floor. Only grows.</summary>
    internal double BarMinWidth { get; private set; }

    internal PlayerBarFit BarFit => _barFit;

    /// <summary>Picks the full, compact or narrow bar (<see cref="PlayerBarLayout"/>)
    /// and narrows the seek line so the side zones keep what they need. The identity
    /// counts while it is hidden too, so the first track doesn't change the fit.</summary>
    private void FitSeekArea()
    {
        // Hidden with nothing loaded, the identity isn't measured by layout; its
        // artwork and actions are, measured here, since their size never changes.
        var artwork = BarIdentity.Children[0];
        if (!BarIdentity.IsVisible)
        {
            artwork.Measure(Size.Infinity);
            BarIdentityActions.Measure(Size.Infinity);
        }
        if (BarFullActions.IsVisible)
            _fullActionsWidth = BarFullActions.DesiredSize.Width;
        if (BarMoreButton.IsVisible)
            _moreWidth = BarMoreButton.DesiredSize.Width;
        else if (_moreWidth == 0)
            _moreWidth = SaveButton.DesiredSize.Width; // the same icon button, until More has shown
        if (VolumeSlider.IsVisible)
            _volumeSliderWidth = VolumeSlider.DesiredSize.Width + VolumeSlider.Margin.Left + VolumeSlider.Margin.Right;
        // The labels themselves: the row's own size can still hold the seek line's
        // previous width, which for a pass counted it as time labels.
        if (!SeekRow.Classes.Contains("timesUnder"))
            _timeLabelsWidth = SeekRow.Children[0].DesiredSize.Width + SeekRow.Children[2].DesiredSize.Width;

        // The mute and volume buttons are the same size, so the modes differ by the slider.
        var compactModes = BarModes.DesiredSize.Width - (VolumeSlider.IsVisible ? _volumeSliderWidth : 0);
        var identityRest = artwork.DesiredSize.Width + 2 * BarIdentity.ColumnSpacing + BarIdentity.Margin.Right;
        var fullSide = Math.Max(identityRest + PlayerBarLayout.MinTitleWidth + _fullActionsWidth,
                                compactModes + _volumeSliderWidth);
        var compactSide = Math.Max(identityRest + PlayerBarLayout.CompactTitleWidth + _moreWidth, compactModes);

        var barWidth = PlayerBarGrid.Bounds.Width;
        SetBarFit(PlayerBarLayout.Fit(barWidth, fullSide, compactSide, _timeLabelsWidth));
        // A long title takes room from the seek line before it trims.
        var titledSide = Math.Max(fullSide, identityRest + PlayerBarLayout.TitleRoom(TitleWidth()) + _fullActionsWidth);
        var width = _barFit switch
        {
            PlayerBarFit.Full => PlayerBarLayout.SeekWidth(barWidth, titledSide, _timeLabelsWidth),
            PlayerBarFit.Compact => PlayerBarLayout.SeekWidth(barWidth, compactSide, _timeLabelsWidth),
            // Below the compact bar's floor: the line stays there, and what the
            // times gave up by moving under it goes to the title.
            _ => PlayerBarLayout.MinSeekWidth,
        };
        if (Math.Abs(width - SeekArea.Width) > 0.5)
            SeekArea.Width = width;

        var minWidth = Math.Ceiling(PlayerBarLayout.MinBarWidth(compactSide, _timeLabelsWidth) +
                                    PlayerBarGrid.Margin.Left + PlayerBarGrid.Margin.Right);
        if (minWidth > BarMinWidth)
        {
            BarMinWidth = minWidth;
            HoldMinWidth();
        }
    }

    /// <summary>The bar title's width untrimmed.</summary>
    private double TitleWidth()
    {
        if (string.IsNullOrEmpty(BarTitle.Text))
            return 0;
        using var layout = new TextLayout(BarTitle.Text,
            new Typeface(BarTitle.FontFamily, BarTitle.FontStyle, BarTitle.FontWeight), BarTitle.FontSize, null);
        return Math.Ceiling(layout.WidthIncludingTrailingWhitespace);
    }

    private void HoldMinWidth()
    {
        var min = PlayerBarLayout.HeldMinimum(ClientSize.Width, BarMinWidth);
        if (Math.Abs(MinWidth - min) > 0.5)
            MinWidth = min;
    }

    /// <summary>The window's minimum height: the title bar, the player bar and the
    /// rest of the frame around the sidebar, plus the sidebar's whole nav list and
    /// account chip. Below it the list scrolls instead of running under the chip.</summary>
    internal double SidebarMinHeight { get; private set; }

    private void FitSidebar()
    {
        var need = Sidebar.Padding.Top + Sidebar.Padding.Bottom +
                   SidebarNavScroll.Margin.Top + SidebarNavScroll.Margin.Bottom + SidebarNav.DesiredSize.Height +
                   SidebarAccount.Margin.Top + SidebarAccount.Margin.Bottom + SidebarAccount.DesiredSize.Height;
        var min = Math.Ceiling(ClientSize.Height - Sidebar.Bounds.Height + need);
        if (Math.Abs(min - SidebarMinHeight) <= 0.5)
            return;
        SidebarMinHeight = min;
        HoldMinHeight();
    }

    private void HoldMinHeight()
    {
        var min = PlayerBarLayout.HeldMinimum(ClientSize.Height, SidebarMinHeight);
        if (Math.Abs(MinHeight - min) > 0.5)
            MinHeight = min;
    }

    private void SetBarFit(PlayerBarFit fit)
    {
        if (fit == _barFit)
            return;
        _barFit = fit;
        var full = fit == PlayerBarFit.Full;
        BarFullActions.IsVisible = full;
        BarMoreButton.IsVisible = !full;
        MuteButton.IsVisible = full;
        VolumeSlider.IsVisible = full;
        VolumeButton.IsVisible = !full;
        SeekRow.Classes.Set("timesUnder", fit == PlayerBarFit.Narrow);
        if (full)
        {
            BarMoreButton.Flyout?.Hide();
            VolumeButton.Flyout?.Hide();
        }
    }

    private void UpdateSeekFill() =>
        SeekFill.Width = PositionSlider.Maximum > 0
            ? SeekArea.Bounds.Width *
              Math.Clamp(PositionSlider.Value / PositionSlider.Maximum, 0, 1)
            : 0;

    /// <summary>Pointer x → slider-range seconds, clamped to the track.</summary>
    private double SeekTarget(double x) =>
        SeekArea.Bounds.Width > 0
            ? Math.Clamp(x / SeekArea.Bounds.Width, 0, 1) * PositionSlider.Maximum
            : PositionSlider.Value;

    /// <summary>Keyboard shortcuts; the reference sheet is on F1.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainViewModel vm)
            return;

        // Never hijack keys from an active text field.
        var typing = FocusManager?.GetFocusedElement() is TextBox;

        switch (e.Key, e.KeyModifiers)
        {
            case (Key.Space, KeyModifiers.None) when !typing:
                vm.Player.TogglePlayPauseCommand.Execute(null);
                break;
            case (Key.Right, KeyModifiers.Control):
                vm.Player.NextCommand.Execute(null);
                break;
            case (Key.Left, KeyModifiers.Control):
                vm.Player.PreviousCommand.Execute(null);
                break;
            case (Key.Right, KeyModifiers.Control | KeyModifiers.Shift):
                _ = vm.Player.SeekByAsync(10);
                break;
            case (Key.Left, KeyModifiers.Control | KeyModifiers.Shift):
                _ = vm.Player.SeekByAsync(-10);
                break;
            case (Key.Up, KeyModifiers.Control):
                vm.Player.Volume = Math.Clamp(vm.Player.Volume + 0.05, 0, 1);
                break;
            case (Key.Down, KeyModifiers.Control):
                vm.Player.Volume = Math.Clamp(vm.Player.Volume - 0.05, 0, 1);
                break;
            case (Key.Left, KeyModifiers.Alt):
                Run(vm.Navigation.BackCommand);
                break;
            case (Key.Right, KeyModifiers.Alt):
                Run(vm.Navigation.ForwardCommand);
                break;
            case (Key.F, KeyModifiers.Control):
                Run(vm.GoSearchCommand);
                break;
            case (Key.OemComma, KeyModifiers.Control):
                Run(vm.GoSettingsCommand);
                break;
            case (Key.B, KeyModifiers.Control):
                Run(vm.ToggleSidebarCommand);
                break;
            case (Key.P, KeyModifiers.Control):
                vm.NowPlaying.ToggleCommand.Execute(null);
                break;
            case (Key.Escape, KeyModifiers.None) when vm.NowPlaying.IsOpen:
                vm.NowPlaying.CloseCommand.Execute(null);
                break;
            case (Key.F1, KeyModifiers.None):
                _ = new ShortcutsDialog().ShowDialog(this);
                break;
            default:
                return;
        }

        e.Handled = true;

        static void Run(System.Windows.Input.ICommand command)
        {
            if (command.CanExecute(null))
                command.Execute(null);
        }
    }

    private System.Threading.Tasks.Task ShowSignInAsync(SessionViewModel session) =>
        new SignInDialog(session.CreateSignInDialog()).ShowDialog<bool>(this);

    private ProfilesDialog? _profilesDialog;

    /// <summary>One Profiles dialog at a time: a second request brings the open one forward.</summary>
    private async System.Threading.Tasks.Task ShowProfilesAsync(ProfilesViewModel profiles)
    {
        if (_profilesDialog is { } open)
        {
            open.Activate();
            return;
        }
        _profilesDialog = new ProfilesDialog(profiles);
        try
        {
            await _profilesDialog.ShowDialog(this);
        }
        finally
        {
            _profilesDialog = null;
        }
    }
}
