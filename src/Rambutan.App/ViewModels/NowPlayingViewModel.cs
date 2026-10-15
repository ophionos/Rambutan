using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Rambutan.App.Services;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

/// <summary>
/// The Now Playing surface: expands over the content region from the player bar.
/// Track state comes from the shared <see cref="PlayerBarViewModel"/>; this VM
/// owns the open/closed state, whether the lyrics pane and the stage are on,
/// and which style the visualizer draws.
/// </summary>
public partial class NowPlayingViewModel : ViewModelBase
{
    private readonly AppSettings? _settings;
    private readonly VisualizerCycler _cycler;
    private readonly Func<TimeSpan, Action, IDisposable> _every;
    private IDisposable? _cycleTimer;
    private IDisposable? _fullscreenTimer;
    private TimeSpan _chromeIdle;
    private TimeSpan _titleAge;
    private bool _isChromeHeld;
    private string? _lastTrackId;

    /// <summary>How often the cycle timer counts playing time.</summary>
    internal static readonly TimeSpan CycleTick = TimeSpan.FromSeconds(1);

    /// <summary>How often fullscreen counts towards hiding its controls and title.</summary>
    internal static readonly TimeSpan FullscreenTick = TimeSpan.FromMilliseconds(250);

    /// <summary>How long fullscreen's controls stay without input.</summary>
    internal static readonly TimeSpan ChromeDelay = TimeSpan.FromSeconds(3);

    /// <summary>How long fullscreen shows a new track's title.</summary>
    internal static readonly TimeSpan TitleCardTime = TimeSpan.FromSeconds(5);

    public PlayerBarViewModel Player { get; }
    public LyricsViewModel Lyrics { get; }

    /// <summary>The visualizer's style and the motion switch; the defaults when
    /// none was given (design time, tests).</summary>
    public AppearanceSettings Appearance { get; }

    /// <summary>The visualizer's colour; null draws in the accent.</summary>
    public VisualizerColors? Colors { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreLyricsOnScreen), nameof(LyricsButtonTip))]
    private bool _isOpen;

    /// <summary>The header toggle: the lyrics overlay, or the artwork alone.
    /// Remembered in <see cref="AppSettings.ShowLyrics"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LyricsToggleTip), nameof(IsLyricsPaneVisible), nameof(AreLyricsOnScreen), nameof(LyricsButtonTip))]
    private bool _showLyrics;

    /// <summary>The header toggle that gives the pane to the visualizer, in
    /// place of the artwork and title. Remembered in
    /// <see cref="AppSettings.ShowVisualizerStage"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StageToggleTip))]
    private bool _showStage;

    /// <summary>The visualizer fills the screen: the window goes fullscreen with
    /// only this pane in it (MainWindow), and the stage shows whatever its own
    /// toggle says. Not remembered: a start in fullscreen would surprise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullscreenTip), nameof(IsWindowed), nameof(IsChromeHidden))]
    private bool _isFullscreen;

    /// <summary>Fullscreen's controls show: after input, until
    /// <see cref="ChromeDelay"/> passes without any, and while held.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTitleCardShown), nameof(IsChromeHidden))]
    private bool _isChromeShown;

    /// <summary>Fullscreen without input for a while: the controls and the
    /// pointer are hidden. The controls stay focusable, and a key shows them.</summary>
    public bool IsChromeHidden => IsFullscreen && !IsChromeShown;

    /// <summary>A track started in fullscreen less than <see cref="TitleCardTime"/> ago.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTitleCardShown))]
    private bool _isTitleFresh;

    public string FullscreenTip => Strings.Get(IsFullscreen ? "nowPlaying.leaveFullscreen" : "nowPlaying.fullscreen");

    /// <summary>Fullscreen's title and artist, over the visualizer: when a track
    /// starts, and with the controls. Not while the artwork and title show.</summary>
    public bool IsTitleCardShown =>
        IsFullscreen && !IsIdentityShown && Player.ShownTrack is not null && (IsChromeShown || IsTitleFresh);

    /// <summary>The header's windowed buttons: the stage toggle and collapse.
    /// Fullscreen always shows the stage, and Esc or its own button leaves it.</summary>
    public bool IsWindowed => !IsFullscreen;

    /// <summary>The lyrics overlay: something is playing and the pane is on.</summary>
    public bool IsLyricsPaneVisible => ShowLyrics && Lyrics.IsShown;

    public string LyricsToggleTip => Strings.Get(ShowLyrics ? "nowPlaying.hideLyrics" : "nowPlaying.showLyrics");

    public string StageToggleTip => Strings.Get(ShowStage ? "nowPlaying.hideStage" : "nowPlaying.showStage");

    /// <summary>The player bar's lyrics button is lit: Now Playing is open with
    /// its lyrics pane on.</summary>
    public bool AreLyricsOnScreen => IsOpen && ShowLyrics;

    public string LyricsButtonTip => Strings.Get(AreLyricsOnScreen ? "nowPlaying.hideLyrics" : "nowPlaying.showLyrics");

    /// <summary>The style the visualizer draws now: the one cycling picked, or
    /// the chosen one (bars for a preset while advanced mode is off).</summary>
    public VisualizerStyle DrawnStyle => _cycler.Current ?? Appearance.ShownStyle;

    /// <summary>The stage: something is playing, and its toggle is on or the
    /// visualizer is fullscreen.</summary>
    public bool IsStageShown => (ShowStage || IsFullscreen) && Player.NowPlaying is not null;

    /// <summary>The radial style draws around the artwork, and only there: with
    /// the lyrics pane open, or the stage on, there is no artwork on screen, and
    /// then the visualizer shows bars instead. Never under reduced motion, which
    /// keeps the band's "paused" note in view.</summary>
    public bool ShowsRing =>
        DrawnStyle == VisualizerStyle.Radial
        && !Appearance.ReduceMotion
        && !IsStageShown
        && !IsLyricsPaneVisible
        && Player.ShownTrack is not null
        && Player.ArtworkUrl is not null;

    /// <summary>The band, under the artwork: one visualizer at a time, so it
    /// gives way to the ring and the stage. Hidden with no track. Under reduced
    /// motion it shows the "paused" note, the stage's too.</summary>
    public bool IsBandShown =>
        Player.NowPlaying is not null && !ShowsRing && (!IsStageShown || Appearance.ReduceMotion);

    /// <summary>The style the band and the stage draw in: the drawn one, or
    /// bars when that is the ring.</summary>
    public VisualizerStyle BandStyle => DrawnStyle == VisualizerStyle.Radial ? VisualizerStyle.Bars : DrawnStyle;

    /// <summary>Whether the band polls the analyzer. A visualizer that isn't
    /// seen doesn't, so its lease lapses.</summary>
    public bool IsBandActive => IsOpen && IsBandShown && !Appearance.ReduceMotion;

    public bool IsRingActive => IsOpen && ShowsRing;

    /// <summary>The stage's visualizer draws: under reduced motion it gives way
    /// to the band's note.</summary>
    public bool IsStageVisualizerShown => IsStageShown && !Appearance.ReduceMotion;

    public bool IsStageActive => IsOpen && IsStageVisualizerShown;

    /// <summary>The artwork and title, which the stage replaces. Fullscreen
    /// under reduced motion keeps them, so the screen at rest isn't empty.</summary>
    public bool IsIdentityShown => !IsStageShown || (IsFullscreen && Appearance.ReduceMotion);

    /// <summary>Lyrics over the stage: the visualizer dims, so the lines stay
    /// readable over it.</summary>
    public double StageOpacity => IsLyricsPaneVisible ? 0.3 : 1;

    /// <summary>An animated preset draws: the one case where drawing in
    /// software costs resolution, which the view then says.</summary>
    public bool IsPresetDrawn => AppearanceSettings.IsPreset(BandStyle);

    /// <summary>The view found that Skia draws without a GPU.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSoftwareNote))]
    private bool _isDrawnInSoftware;

    /// <summary>The note that a preset draws at a lower resolution, because
    /// there is no GPU: only while one draws.</summary>
    public bool ShowsSoftwareNote => IsDrawnInSoftware && IsPresetDrawn && (IsBandActive || IsStageActive);

    // Design-time
    public NowPlayingViewModel() : this(new PlayerBarViewModel(), new LyricsViewModel())
    {
        IsOpen = true;
    }

    /// <param name="every">Calls the action repeatedly at the interval until
    /// disposed; a dispatcher timer when null.</param>
    /// <param name="random">For cycling: a number from 0 up to its argument.</param>
    public NowPlayingViewModel(
        PlayerBarViewModel player,
        LyricsViewModel? lyrics = null,
        AppSettings? settings = null,
        AppearanceSettings? appearance = null,
        VisualizerColors? colors = null,
        Func<TimeSpan, Action, IDisposable>? every = null,
        Func<int, int>? random = null)
    {
        Player = player;
        Appearance = appearance ?? new AppearanceSettings();
        Colors = colors;
        _every = every ?? Every;
        _cycler = new VisualizerCycler(random);
        Appearance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppearanceSettings.VisualizerStyle)
                or nameof(AppearanceSettings.VisualizerAdvanced) or nameof(AppearanceSettings.VisualizerCycle))
            {
                _cycler.Reset();
                OnVisualizerChanged();
            }
            else if (e.PropertyName is nameof(AppearanceSettings.ReduceMotion))
            {
                OnVisualizerChanged();
            }
        };
        _lastTrackId = Player.NowPlaying?.VideoId;
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PlayerBarViewModel.NowPlaying))
                OnTrackChanged();
            if (e.PropertyName is nameof(PlayerBarViewModel.ArtworkUrl)
                or nameof(PlayerBarViewModel.ShownTrack) or nameof(PlayerBarViewModel.NowPlaying))
                OnVisualizerChanged();
        };
        Lyrics = lyrics ?? new LyricsViewModel(
            (_, _) => System.Threading.Tasks.Task.FromResult(Rambutan.Lyrics.Providers.LyricsLookup.NotFound),
            _ => System.Threading.Tasks.Task.CompletedTask);
        Lyrics.PropertyChanged += OnLyricsChanged;

        // Assigned after _settings so the change handlers have somewhere to write.
        _settings = settings;
        ShowLyrics = settings?.ShowLyrics ?? false;
        ShowStage = settings?.ShowVisualizerStage ?? false;
        Lyrics.IsEnabled = ShowLyrics;
        Strings.WhenChanged(this, static vm =>
        {
            vm.OnPropertyChanged(nameof(LyricsToggleTip));
            vm.OnPropertyChanged(nameof(LyricsButtonTip));
            vm.OnPropertyChanged(nameof(StageToggleTip));
            vm.OnPropertyChanged(nameof(FullscreenTip));
        });
    }

    private static IDisposable Every(TimeSpan interval, Action action) =>
        DispatcherTimer.Run(() =>
        {
            action();
            return true;
        }, interval);

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.IsShown))
        {
            OnPropertyChanged(nameof(IsLyricsPaneVisible));
            OnVisualizerChanged();
        }
    }

    private void OnVisualizerChanged()
    {
        OnPropertyChanged(nameof(DrawnStyle));
        OnPropertyChanged(nameof(IsStageShown));
        OnPropertyChanged(nameof(ShowsRing));
        OnPropertyChanged(nameof(IsBandShown));
        OnPropertyChanged(nameof(BandStyle));
        OnPropertyChanged(nameof(IsBandActive));
        OnPropertyChanged(nameof(IsRingActive));
        OnPropertyChanged(nameof(IsStageVisualizerShown));
        OnPropertyChanged(nameof(IsStageActive));
        OnPropertyChanged(nameof(IsIdentityShown));
        OnPropertyChanged(nameof(StageOpacity));
        OnPropertyChanged(nameof(IsPresetDrawn));
        OnPropertyChanged(nameof(ShowsSoftwareNote));
        OnPropertyChanged(nameof(IsTitleCardShown));
        UpdateCycleTimer();
    }

    /// <summary>What cycling picks from: what the picker offers, without the
    /// ring on the stage, where it would only be bars again.</summary>
    internal IReadOnlyList<VisualizerStyle> CycleChoices()
    {
        var offered = AppearanceSettings.Offered(Appearance.VisualizerAdvanced);
        if (!IsStageShown)
            return offered;
        return Array.FindAll(offered, style => style != VisualizerStyle.Radial);
    }

    /// <summary>Random cycling counts time only while a visualizer is seen, so
    /// the timer runs only then; a hidden one builds no renderers.</summary>
    private void UpdateCycleTimer()
    {
        var run = Appearance.VisualizerCycle == VisualizerCycle.Random
            && (IsBandActive || IsRingActive || IsStageActive);
        if (run && _cycleTimer is null)
        {
            _cycleTimer = _every(CycleTick, () => AdvanceCycle(CycleTick));
        }
        else if (!run && _cycleTimer is not null)
        {
            _cycleTimer.Dispose();
            _cycleTimer = null;
        }
    }

    /// <summary>A tick of the cycle timer: <paramref name="elapsed"/> counts
    /// only while something plays.</summary>
    internal void AdvanceCycle(TimeSpan elapsed)
    {
        if (Appearance.VisualizerCycle != VisualizerCycle.Random || !Player.IsPlaying)
            return;
        if (_cycler.Elapse(elapsed, CycleChoices(), DrawnStyle))
            OnVisualizerChanged();
    }

    private void OnTrackChanged()
    {
        var id = Player.NowPlaying?.VideoId;
        if (id is null || id == _lastTrackId)
            return;
        var first = _lastTrackId is null;
        _lastTrackId = id;
        if (IsFullscreen)
            FreshenTitle();
        // The first track of a session keeps the chosen style.
        if (!first && Appearance.VisualizerCycle == VisualizerCycle.TrackChange
            && _cycler.Next(CycleChoices(), DrawnStyle))
            OnVisualizerChanged();
    }

    partial void OnIsOpenChanged(bool value)
    {
        // Fullscreen is this pane: closing it leaves fullscreen too.
        if (!value)
            IsFullscreen = false;
        OnVisualizerChanged();
    }

    partial void OnIsFullscreenChanged(bool value)
    {
        if (value)
        {
            IsOpen = true;
            // The way out shows first, with what is playing.
            ShowChrome();
            FreshenTitle();
        }
        else
        {
            _isChromeHeld = false;
            IsChromeShown = false;
            IsTitleFresh = false;
            UpdateFullscreenTimer();
        }
        OnVisualizerChanged();
    }

    /// <summary>Input in fullscreen: the pointer moved, or a key or a click
    /// came. Shows the controls and starts their wait again.</summary>
    public void ShowChrome()
    {
        if (!IsFullscreen)
            return;
        _chromeIdle = TimeSpan.Zero;
        IsChromeShown = true;
        UpdateFullscreenTimer();
    }

    /// <summary>The pointer is over the controls, or the keyboard focus is in
    /// them: they stay until that ends, and their wait starts again then.</summary>
    public void HoldChrome(bool held)
    {
        _isChromeHeld = held;
        if (held)
            ShowChrome();
        else
            _chromeIdle = TimeSpan.Zero;
    }

    private void FreshenTitle()
    {
        _titleAge = TimeSpan.Zero;
        IsTitleFresh = true;
        UpdateFullscreenTimer();
    }

    /// <summary>Runs only while something in fullscreen waits to hide.</summary>
    private void UpdateFullscreenTimer()
    {
        var run = IsFullscreen && (IsChromeShown || IsTitleFresh);
        if (run && _fullscreenTimer is null)
        {
            _fullscreenTimer = _every(FullscreenTick, () => AdvanceFullscreen(FullscreenTick));
        }
        else if (!run && _fullscreenTimer is not null)
        {
            _fullscreenTimer.Dispose();
            _fullscreenTimer = null;
        }
    }

    /// <summary>A tick of the fullscreen timer.</summary>
    internal void AdvanceFullscreen(TimeSpan elapsed)
    {
        if (IsChromeShown && !_isChromeHeld)
        {
            _chromeIdle += elapsed;
            if (_chromeIdle >= ChromeDelay)
                IsChromeShown = false;
        }
        if (IsTitleFresh)
        {
            _titleAge += elapsed;
            if (_titleAge >= TitleCardTime)
                IsTitleFresh = false;
        }
        UpdateFullscreenTimer();
    }

    partial void OnShowLyricsChanged(bool value)
    {
        OnVisualizerChanged();
        Lyrics.IsEnabled = value;
        if (_settings is not null)
            _settings.ShowLyrics = value;
    }

    partial void OnShowStageChanged(bool value)
    {
        OnVisualizerChanged();
        if (_settings is not null)
            _settings.ShowVisualizerStage = value;
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void ToggleLyrics() => ShowLyrics = !ShowLyrics;

    [RelayCommand]
    private void ToggleStage() => ShowStage = !ShowStage;

    [RelayCommand]
    private void ToggleFullscreen() => IsFullscreen = !IsFullscreen;

    /// <summary>The player bar's lyrics button: opens Now Playing with its lyrics
    /// pane on, and closes it again when that is what's on screen. Turning the
    /// pane on is the same choice as the header toggle (lyrics are off until the
    /// listener asks), so it is remembered the same way.</summary>
    [RelayCommand]
    private void OpenLyrics()
    {
        if (AreLyricsOnScreen)
        {
            IsOpen = false;
            return;
        }
        ShowLyrics = true;
        IsOpen = true;
    }
}
