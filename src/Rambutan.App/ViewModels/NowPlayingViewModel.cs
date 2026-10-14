using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Services;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

/// <summary>
/// The Now Playing surface: expands over the content region from the player bar.
/// Track state comes from the shared <see cref="PlayerBarViewModel"/>; this VM
/// owns the open/closed state and whether the lyrics pane is on.
/// </summary>
public partial class NowPlayingViewModel : ViewModelBase
{
    private readonly AppSettings? _settings;

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

    /// <summary>The lyrics overlay: something is playing and the pane is on.</summary>
    public bool IsLyricsPaneVisible => ShowLyrics && Lyrics.IsShown;

    public string LyricsToggleTip => Strings.Get(ShowLyrics ? "nowPlaying.hideLyrics" : "nowPlaying.showLyrics");

    /// <summary>The player bar's lyrics button is lit: Now Playing is open with
    /// its lyrics pane on.</summary>
    public bool AreLyricsOnScreen => IsOpen && ShowLyrics;

    public string LyricsButtonTip => Strings.Get(AreLyricsOnScreen ? "nowPlaying.hideLyrics" : "nowPlaying.showLyrics");

    /// <summary>The radial style draws around the artwork, and only there: with
    /// the lyrics pane open there is no artwork on the stage, and then the band
    /// shows bars instead. Never under reduced motion, which keeps the band's
    /// "paused" note in view.</summary>
    public bool ShowsRing =>
        Appearance.VisualizerStyle == VisualizerStyle.Radial
        && !Appearance.ReduceMotion
        && !IsLyricsPaneVisible
        && Player.ShownTrack is not null
        && Player.ArtworkUrl is not null;

    /// <summary>The band, under the stage: one visualizer at a time, so it
    /// gives way to the ring. Hidden with no track.</summary>
    public bool IsBandShown => Player.NowPlaying is not null && !ShowsRing;

    /// <summary>The band's style: the chosen one, or bars when the chosen one
    /// is the ring.</summary>
    public VisualizerStyle BandStyle =>
        Appearance.VisualizerStyle == VisualizerStyle.Radial ? VisualizerStyle.Bars : Appearance.VisualizerStyle;

    /// <summary>Whether the band polls the analyzer. A visualizer that isn't
    /// seen doesn't, so its lease lapses.</summary>
    public bool IsBandActive => IsOpen && IsBandShown && !Appearance.ReduceMotion;

    public bool IsRingActive => IsOpen && ShowsRing;

    // Design-time
    public NowPlayingViewModel() : this(new PlayerBarViewModel(), new LyricsViewModel())
    {
        IsOpen = true;
    }

    public NowPlayingViewModel(
        PlayerBarViewModel player,
        LyricsViewModel? lyrics = null,
        AppSettings? settings = null,
        AppearanceSettings? appearance = null,
        VisualizerColors? colors = null)
    {
        Player = player;
        Appearance = appearance ?? new AppearanceSettings();
        Colors = colors;
        Appearance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppearanceSettings.VisualizerStyle) or nameof(AppearanceSettings.ReduceMotion))
                OnVisualizerChanged();
        };
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PlayerBarViewModel.ArtworkUrl)
                or nameof(PlayerBarViewModel.ShownTrack) or nameof(PlayerBarViewModel.NowPlaying))
                OnVisualizerChanged();
        };
        Lyrics = lyrics ?? new LyricsViewModel(
            (_, _) => System.Threading.Tasks.Task.FromResult(Rambutan.Lyrics.Providers.LyricsLookup.NotFound),
            _ => System.Threading.Tasks.Task.CompletedTask);
        Lyrics.PropertyChanged += OnLyricsChanged;

        // Assigned after _settings so the change handler has somewhere to write.
        _settings = settings;
        ShowLyrics = settings?.ShowLyrics ?? false;
        Lyrics.IsEnabled = ShowLyrics;
        Strings.WhenChanged(this, static vm =>
        {
            vm.OnPropertyChanged(nameof(LyricsToggleTip));
            vm.OnPropertyChanged(nameof(LyricsButtonTip));
        });
    }

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
        OnPropertyChanged(nameof(ShowsRing));
        OnPropertyChanged(nameof(IsBandShown));
        OnPropertyChanged(nameof(BandStyle));
        OnPropertyChanged(nameof(IsBandActive));
        OnPropertyChanged(nameof(IsRingActive));
    }

    partial void OnIsOpenChanged(bool value) => OnVisualizerChanged();

    partial void OnShowLyricsChanged(bool value)
    {
        OnVisualizerChanged();
        Lyrics.IsEnabled = value;
        if (_settings is not null)
            _settings.ShowLyrics = value;
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void ToggleLyrics() => ShowLyrics = !ShowLyrics;

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
