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

    // Design-time
    public NowPlayingViewModel() : this(new PlayerBarViewModel(), new LyricsViewModel())
    {
        IsOpen = true;
    }

    public NowPlayingViewModel(PlayerBarViewModel player, LyricsViewModel? lyrics = null, AppSettings? settings = null)
    {
        Player = player;
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
            OnPropertyChanged(nameof(IsLyricsPaneVisible));
    }

    partial void OnShowLyricsChanged(bool value)
    {
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
