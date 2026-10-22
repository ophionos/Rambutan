using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Converters;
using Rambutan.Core.Catalog;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;
using Rambutan.App.Localization;
using Rambutan.Core.Local;

namespace Rambutan.App.ViewModels;

public partial class PlayerBarViewModel : ViewModelBase
{
    private readonly PlaybackCoordinator _coordinator;
    private readonly QueueModel _queue;
    private readonly TrackRatings? _ratings;
    private readonly ToastHostViewModel? _toasts;

    [ObservableProperty] private string _title = Strings.Get("player.nothingPlaying");
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string? _artworkUrl;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseTip))]
    private bool _isPlaying;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private double _bufferedFraction;
    /// <summary>The two ends of the seek line, either side of the centred transport.
    /// Separate strings so each sits in its own fixed slot and the
    /// transport doesn't shift as the digits change.</summary>
    [ObservableProperty] private string _elapsedDisplay = "0:00";
    [ObservableProperty] private string _durationDisplay = "0:00";
    [ObservableProperty] private Rambutan.Core.Models.TrackInfo? _nowPlaying;

    /// <summary>The track the bar shows: the loaded one, else the queue's current
    /// one, which Play starts from 0:00. <see cref="NowPlaying"/> stays the loaded track only: pages mark
    /// it as playing, and only it can seek.</summary>
    [ObservableProperty] private Rambutan.Core.Models.TrackInfo? _shownTrack;

    /// <summary><see cref="ShownTrack"/> as its names show: a local file with
    /// what its tags lack filled from its match (<see cref="ShowAs"/>). The
    /// links and actions stay with <see cref="ShownTrack"/>.</summary>
    [ObservableProperty] private Rambutan.Core.Models.TrackInfo? _displayedTrack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteTip))]
    private bool _isMuted;

    /// <summary>Anything to play at all. Transport is disabled without it, since
    /// pressing it would do nothing.</summary>
    [ObservableProperty] private bool _hasQueue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepeatOn))]
    [NotifyPropertyChangedFor(nameof(IsRepeatOne))]
    [NotifyPropertyChangedFor(nameof(RepeatTip))]
    private RepeatMode _repeat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleTip))]
    private bool _isShuffleOn;

    public string ShuffleTip => Strings.Get(IsShuffleOn ? "player.shuffleOn" : "player.shuffleOff");

    // Tooltips that say what the button does, or is, now: the icon swaps are
    // visual only, and a screen reader takes the button's name from its tip
    // (Controls/AccessibleNames).
    public string PlayPauseTip => Strings.Get(IsPlaying ? "player.pause" : "player.play");
    public string MuteTip => Strings.Get(IsMuted ? "player.unmute" : "player.mute");
    public string RepeatTip => Strings.Get(Repeat switch
    {
        RepeatMode.All => "player.repeatAll",
        RepeatMode.One => "player.repeatOne",
        _ => "player.repeatOff",
    });

    private void OnLanguageChanged()
    {
        if (ShownTrack is null)
            Title = Strings.Get("player.nothingPlaying");
        OnPropertyChanged(nameof(ShuffleTip));
        OnPropertyChanged(nameof(PlayPauseTip));
        OnPropertyChanged(nameof(MuteTip));
        OnPropertyChanged(nameof(RepeatTip));
    }

    /// <summary>Volume to restore when unmuting; mute is a UI concept, not engine state.</summary>
    private double _volumeBeforeMute = 1.0;

    public bool IsRepeatOn => Repeat is not RepeatMode.Off;
    public bool IsRepeatOne => Repeat is RepeatMode.One;

    /// <summary>Set by the view while the user drags the seek thumb.</summary>
    public bool IsSeeking { get; set; }

    public event Action? SettingsEdited;

    // Design-time
    public PlayerBarViewModel() : this(null!, new QueueModel(), null)
    {
        Title = "Instant Crush (feat. Julian Casablancas)";
        Artist = "Daft Punk • Random Access Memories";
        PositionSeconds = 83;
        DurationSeconds = 338;
        BufferedFraction = 0.4;
        ElapsedDisplay = "1:23";
        DurationDisplay = "5:38";
    }

    /// <param name="localFilesChanged">Subscribes to changes of the local index,
    /// after which a local track's cover is read again.</param>
    public PlayerBarViewModel(
        PlaybackCoordinator coordinator, QueueModel queue, TrackRatings? ratings,
        ToastHostViewModel? toasts = null, Action<Action>? localFilesChanged = null)
    {
        _coordinator = coordinator;
        _queue = queue;
        _ratings = ratings;
        _toasts = toasts;
        _repeat = queue.Repeat;
        _isShuffleOn = queue.Shuffle;
        _hasQueue = queue.Items.Count > 0;
        Show(queue.Current);
        // A language change re-reads the tips and the idle title.
        Strings.WhenChanged(this, static vm => vm.OnLanguageChanged());
        queue.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            HasQueue = _queue.Items.Count > 0;
            IsShuffleOn = _queue.Shuffle;
            // Either can change from elsewhere too: the OS media session (MPRIS).
            Repeat = _queue.Repeat;
            if (NowPlaying is null)
                Show(_queue.Current);
        });
        localFilesChanged?.Invoke(() => Dispatcher.UIThread.Post(RefreshArtwork));

        if (coordinator is null)
            return;

        coordinator.StateChanged += state => Dispatcher.UIThread.Post(() => OnStateChanged(state));
        coordinator.NowPlayingChanged += track => Dispatcher.UIThread.Post(() =>
        {
            NowPlaying = track;
            Show(track ?? _queue.Current, force: true);
        });
        coordinator.ProgressChanged += progress => Dispatcher.UIThread.Post(() => OnProgress(progress));
        // Another surface (the phone remote) can set the volume; the slider follows.
        coordinator.VolumeChanged += _ => Dispatcher.UIThread.Post(OnVolumeChanged);
    }

    private void OnVolumeChanged()
    {
        // Read the volume now, not the event's: a queued change from a slider drag
        // can land after a mute and must not clear it.
        if (IsMuted && Volume > 0)
            IsMuted = false;
        OnPropertyChanged(nameof(Volume));
    }

    public double Volume
    {
        get => _coordinator?.Volume ?? 1.0;
        set
        {
            if (_coordinator is not null)
                _coordinator.Volume = value;

            // Dragging the slider away from zero is an unmute.
            if (value > 0 && IsMuted)
                IsMuted = false;

            OnPropertyChanged(); // keep the slider current when set from shortcuts
            SettingsEdited?.Invoke();
        }
    }

    /// <summary>
    /// The bar's thumbs must show the real rating, but search and radio rows
    /// don't carry one. Ask once per track; the thumbs update via TrackRatings.
    /// A failed lookup leaves them at "unknown" (drawn unrated), which is what
    /// they showed before, and is logged rather than put on the strip: the
    /// user didn't ask for anything.
    /// </summary>
    private async Task LookUpRatingAsync(Rambutan.Core.Models.TrackInfo track)
    {
        if (_ratings is null)
            return;
        try
        {
            await _ratings.LookUpAsync(track);
        }
        catch (Exception ex)
        {
            AppLog.Write("ratings", $"lookup for {track.VideoId} failed: {ex.Message}");
        }
    }

    /// <summary>Puts <paramref name="track"/> in the bar's identity slot, from 0:00;
    /// null is "Nothing playing". Unless forced (the loaded track changed), the
    /// same track again changes nothing, so a queue edit doesn't redo the line.</summary>
    private void Show(Rambutan.Core.Models.TrackInfo? track, bool force = false)
    {
        if (!force && ReferenceEquals(track, ShownTrack))
            return;
        ShownTrack = track;
        DisplayedTrack = Shown(track);
        Title = track?.Title ?? Strings.Get("player.nothingPlaying");
        Artist = Describe(DisplayedTrack);
        ArtworkUrl = Rambutan.App.Converters.ArtworkConverter.For(track);
        if (track is not null)
            _ = LookUpRatingAsync(track);
        ResetProgress(track);
    }

    /// <summary>A local track as shown: with what its tags lack filled from its
    /// YouTube Music match (<see cref="YouTubeMatches.Fill"/>). Null shows it as it is.</summary>
    public Func<Rambutan.Core.Models.TrackInfo, Rambutan.Core.Models.TrackInfo>? ShowAs { get; set; }

    /// <summary>Reads the shown local track's cover and names again: a scan can
    /// give the file another cover, or take it away, and a match can fill them.
    /// Public so tests can drive it.</summary>
    public void RefreshArtwork()
    {
        if (ShownTrack is { } track && LocalIds.IsLocal(track.VideoId))
        {
            ArtworkUrl = Rambutan.App.Converters.ArtworkConverter.For(track);
            DisplayedTrack = Shown(track);
            Artist = Describe(DisplayedTrack);
        }
    }

    private Rambutan.Core.Models.TrackInfo? Shown(Rambutan.Core.Models.TrackInfo? track) =>
        track is not null && ShowAs is { } showAs && LocalIds.IsLocal(track.VideoId) ? showAs(track) : track;

    /// <summary>"Artist • Album", the second line under the title.</summary>
    private static string Describe(Rambutan.Core.Models.TrackInfo? track) => track switch
    {
        null => "",
        { Album: { Length: > 0 } album } => $"{track.ArtistDisplay} • {album}",
        _ => track.ArtistDisplay,
    };

    private void OnStateChanged(PlaybackState state)
    {
        IsPlaying = state is PlaybackState.Playing or PlaybackState.Buffering;
        IsBuffering = state is PlaybackState.Buffering or PlaybackState.Loading;
    }

    private void OnProgress(PlaybackProgress progress)
    {
        var duration = progress.Duration ?? TimeSpan.Zero;
        DurationSeconds = Math.Max(1, duration.TotalSeconds);
        if (!IsSeeking)
            PositionSeconds = progress.Position.TotalSeconds;

        BufferedFraction = duration > TimeSpan.Zero
            ? Math.Clamp((progress.Position + progress.Buffered).TotalSeconds / duration.TotalSeconds, 0, 1)
            : 0;

        ElapsedDisplay = FormatTime(progress.Position);
        DurationDisplay = FormatTime(duration);
    }

    /// <summary>
    /// A new track starts its seek line and times from zero, with its listed
    /// length, rather than showing the previous track's numbers beside the new
    /// title until the first progress report arrives.
    /// </summary>
    public void ResetProgress(Rambutan.Core.Models.TrackInfo? track)
    {
        var duration = track?.Duration ?? TimeSpan.Zero;
        if (!IsSeeking)
            PositionSeconds = 0;
        DurationSeconds = Math.Max(1, duration.TotalSeconds);
        BufferedFraction = 0;
        ElapsedDisplay = FormatTime(TimeSpan.Zero);
        DurationDisplay = FormatTime(duration);
    }

    /// <summary>"m:ss", or "h:mm:ss" from an hour up: the app's one clock format
    /// (<see cref="DurationFormat.Clock"/>). A negative time (a seek
    /// racing a position report) reads as zero rather than "-0:01".</summary>
    public static string FormatTime(TimeSpan time) => DurationFormat.Clock(time);

    [RelayCommand]
    private void ToggleMute()
    {
        if (IsMuted)
        {
            IsMuted = false;
            Volume = _volumeBeforeMute > 0 ? _volumeBeforeMute : 1.0;
            return;
        }

        _volumeBeforeMute = Volume;
        Volume = 0;      // clears IsMuted via the setter's unmute guard...
        IsMuted = true;  // ...so flag it after, not before
    }

    /// <summary>Off → All → One → Off.</summary>
    [RelayCommand]
    private void CycleRepeat()
    {
        Repeat = Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        _queue.Repeat = Repeat;
    }

    /// <summary>
    /// Shuffle is a mode, like Repeat: on shuffles what's ahead and
    /// keeps shuffling new queues, off restores the original order. QueueModel
    /// holds the semantics. The toast says what happened to the queue, because a
    /// reorder in a panel that may be collapsed is otherwise invisible.
    /// </summary>
    [RelayCommand]
    private void ToggleShuffle()
    {
        _queue.SetShuffle(!_queue.Shuffle);
        IsShuffleOn = _queue.Shuffle;

        var upcoming = Math.Max(0, _queue.Items.Count - _queue.CurrentIndex - 1);
        _toasts?.Show((IsShuffleOn, upcoming) switch
        {
            (true, <= 1) => Strings.Get("toast.shuffle.on"),
            (true, _) => Strings.Format("toast.shuffle.onShuffled", ("count", upcoming)),
            (false, 0) => Strings.Get("toast.shuffle.off"),
            (false, _) => Strings.Get("toast.shuffle.offRestored"),
        });
    }

    [RelayCommand]
    private void TogglePlayPause() => _coordinator?.TogglePlayPause();

    // Concurrent executions stay allowed: the default AsyncRelayCommand disables
    // itself mid-flight, which drops rapid skips — the coordinator's gate exists
    // precisely to let the newest command supersede the one still loading.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Next() => _coordinator?.NextAsync() ?? Task.CompletedTask;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Previous() => _coordinator?.PreviousAsync() ?? Task.CompletedTask;

    public Task SeekToAsync(double seconds) =>
        _coordinator?.SeekAsync(TimeSpan.FromSeconds(seconds)) ?? Task.CompletedTask;

    public Task SeekByAsync(double deltaSeconds) =>
        NowPlaying is null
            ? Task.CompletedTask
            : SeekToAsync(Math.Clamp(PositionSeconds + deltaSeconds, 0, DurationSeconds));
}
