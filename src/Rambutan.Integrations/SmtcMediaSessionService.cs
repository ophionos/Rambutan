#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Rambutan.Integrations;

/// <summary>
/// SMTC adapter. Uses an idle <see cref="MediaPlayer"/>'s transport
/// controls — the standard desktop route that sidesteps the
/// ISystemMediaTransportControlsInterop hwnd plumbing. The player itself never
/// plays anything; Rambutan.Audio renders all audio.
/// </summary>
/// <param name="localCover">A local track's kept cover by id (null for none);
/// SMTC gets it as bytes, since it has no URL.</param>
public sealed class SmtcMediaSessionService(Func<string, string?>? localCover = null) : IMediaSessionService
{
    private MediaPlayer? _player;
    private SystemMediaTransportControls? _smtc;
    private TrackInfo? _track;
    private TimeSpan _lastPushedPosition = TimeSpan.MinValue;
    private TimeSpan? _lastPushedDuration;

    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;

    // SMTC sends play and pause through PlayPauseRequested, and has no seek,
    // raise or quit wired here.
    public event Action? PlayRequested { add { } remove { } }
    public event Action? PauseRequested { add { } remove { } }
    public event Action? StopRequested { add { } remove { } }
    public event Action<TimeSpan>? SeekRequested { add { } remove { } }
    public event Action? RaiseRequested { add { } remove { } }
    public event Action? QuitRequested { add { } remove { } }
    public event Action<double>? VolumeRequested { add { } remove { } }
    // SMTC can carry shuffle and repeat, but Windows' own flyout shows neither.
    public event Action<bool>? ShuffleRequested { add { } remove { } }
    public event Action<RepeatMode>? RepeatRequested { add { } remove { } }

    public void Initialize(nint windowHandle)
    {
        _player = new MediaPlayer();
        _player.CommandManager.IsEnabled = false; // we drive the SMTC directly

        _smtc = _player.SystemMediaTransportControls;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += OnButtonPressed;
        _smtc.IsEnabled = true;
    }

    private void OnButtonPressed(
        SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
            case SystemMediaTransportControlsButton.Pause:
                PlayPauseRequested?.Invoke();
                break;
            case SystemMediaTransportControlsButton.Next:
                NextRequested?.Invoke();
                break;
            case SystemMediaTransportControlsButton.Previous:
                PreviousRequested?.Invoke();
                break;
        }
    }

    public void UpdateNowPlaying(TrackInfo? track)
    {
        if (_smtc is null)
            return;

        _track = track;
        var updater = _smtc.DisplayUpdater;
        if (track is null)
        {
            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
            updater.ClearAll();
            updater.Update();
            return;
        }

        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = track.Title;
        updater.MusicProperties.Artist = track.ArtistDisplay;
        updater.MusicProperties.AlbumTitle = track.Album ?? "";
        updater.Thumbnail = Thumbnail(SmtcArtwork.For(track, localCover));
        updater.Update();
    }

    public void UpdateArtwork()
    {
        if (_smtc is null || _track is null)
            return;
        var updater = _smtc.DisplayUpdater;
        updater.Thumbnail = Thumbnail(SmtcArtwork.For(_track, localCover));
        updater.Update();
    }

    /// <summary>
    /// A cover file is read here, once per track and cover, rather than handed over as a
    /// file: <c>CreateFromUri</c> isn't documented to take <c>file:</c>, and a
    /// <c>StorageFile</c> only comes asynchronously. The bytes go into a WinRT
    /// <see cref="InMemoryRandomAccessStream"/>: Windows showed no picture from
    /// a stream over a <see cref="MemoryStream"/>. Writing to it completes at
    /// once, so waiting for it here doesn't hold the UI thread. A file that
    /// can't be read leaves the overlay without a picture.
    /// </summary>
    private static RandomAccessStreamReference? Thumbnail(SmtcArtwork artwork)
    {
        if (artwork.Url is { } url)
            return RandomAccessStreamReference.CreateFromUri(url);
        if (artwork.File is not { } path)
            return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var stream = new InMemoryRandomAccessStream();
            stream.WriteAsync(bytes.AsBuffer()).AsTask().GetAwaiter().GetResult();
            stream.Seek(0);
            return RandomAccessStreamReference.CreateFromStream(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void UpdatePlaybackState(SessionPlaybackStatus status)
    {
        if (_smtc is not null)
        {
            _smtc.PlaybackStatus = status switch
            {
                SessionPlaybackStatus.Playing => MediaPlaybackStatus.Playing,
                SessionPlaybackStatus.Paused => MediaPlaybackStatus.Paused,
                _ => MediaPlaybackStatus.Stopped,
            };
        }
    }

    // SMTC has no volume: Windows sets each app's volume in its own mixer.
    public void UpdateVolume(double volume) { }

    public void UpdateShuffle(bool shuffle) { }

    public void UpdateRepeat(RepeatMode repeat) { }

    public void UpdateTimeline(TimeSpan position, TimeSpan? duration)
    {
        if (_smtc is null)
            return;

        // Progress ticks arrive several times a second; SMTC only needs ~1 Hz.
        if (duration == _lastPushedDuration &&
            Math.Abs((position - _lastPushedPosition).TotalSeconds) < 1)
        {
            return;
        }

        _lastPushedPosition = position;
        _lastPushedDuration = duration;
        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            Position = position,
            MaxSeekTime = duration ?? position,
            EndTime = duration ?? position,
        });
    }

    public void Dispose()
    {
        if (_smtc is not null)
        {
            _smtc.ButtonPressed -= OnButtonPressed;
            _smtc.IsEnabled = false;
            _smtc = null;
        }

        _player?.Dispose();
        _player = null;
    }
}
#endif
