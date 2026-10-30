using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.App.Localization;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Integrations.Notifications;

namespace Rambutan.App.Services;

/// <summary>
/// Shows a system notification when the track changes, as the settings and
/// <see cref="TrackNotificationRule"/> say. The coordinator's events arrive on
/// its threads; everything here runs on the UI thread, through
/// <paramref name="post"/>.
/// </summary>
/// <param name="appHasFocus">An app window is the active window.</param>
/// <param name="fill">What a local file's tags lack, from its YouTube Music
/// match (<see cref="YouTubeMatches.Fill"/>), as the media session shows it.</param>
/// <param name="coverFile">The track's cover as a file on this computer, or null.</param>
/// <param name="coverDirectory">Where the notification's copy of the cover goes.</param>
public sealed class TrackNotifications : IDisposable
{
    /// <summary>A cover that isn't on disk by then is left out, rather than
    /// holding the notification back.</summary>
    public static readonly TimeSpan CoverWait = TimeSpan.FromSeconds(2);

    private readonly PlaybackCoordinator _coordinator;
    private readonly AppSettings _settings;
    private readonly ITrackNotifier _notifier;
    private readonly Func<bool> _appHasFocus;
    private readonly Func<TrackInfo, TrackInfo> _fill;
    private readonly Func<TrackInfo, CancellationToken, Task<string?>> _coverFile;
    private readonly string _coverDirectory;
    private readonly Action<Action> _post;
    private readonly TrackNotificationRule _rule = new();
    // Bumped by each start and stop, so a cover still loading for an older one is dropped.
    private int _generation;

    public TrackNotifications(
        PlaybackCoordinator coordinator, AppSettings settings, ITrackNotifier notifier,
        Func<bool> appHasFocus, Func<TrackInfo, TrackInfo> fill,
        Func<TrackInfo, CancellationToken, Task<string?>> coverFile, string coverDirectory,
        Action<Action> post)
    {
        _coordinator = coordinator;
        _settings = settings;
        _notifier = notifier;
        _appHasFocus = appHasFocus;
        _fill = fill;
        _coverFile = coverFile;
        _coverDirectory = coverDirectory;
        _post = post;
        coordinator.TrackStarted += OnTrackStarted;
        coordinator.NowPlayingChanged += OnNowPlayingChanged;
    }

    /// <summary>The task of the notification being prepared (tests wait on it).</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    private void OnTrackStarted(TrackStart start) => _post(() =>
    {
        var generation = ++_generation;
        if (_notifier.CanShow &&
            _rule.ShouldShow(start, _settings.NotifyOnTrackChange, _settings.NotifyOnItsOwnWhileFocused, _appHasFocus()))
        {
            Pending = ShowAsync(_fill(start.Track), generation);
        }
    });

    private void OnNowPlayingChanged(TrackInfo? track)
    {
        if (track is not null)
            return;
        _post(() =>
        {
            ++_generation;
            _rule.Reset();
            _notifier.Close();
        });
    }

    private async Task ShowAsync(TrackInfo track, int generation)
    {
        string? cover = null;
        try
        {
            using var wait = new CancellationTokenSource(CoverWait);
            if (await _coverFile(track, wait.Token) is { } file)
                cover = NotificationCover.Copy(file, _coverDirectory);
        }
        catch (Exception ex)
        {
            AppLog.Write("notify", $"no cover for {track.VideoId}: {ex.Message}");
        }
        // A newer track started, or playback stopped, while the cover loaded.
        if (generation != _generation || !_settings.NotifyOnTrackChange)
            return;
        _notifier.Show(new TrackNotification(track.Title, Body(track), cover));
    }

    /// <summary>The artists and the album, whichever the track has.</summary>
    public static string Body(TrackInfo track)
    {
        var artist = track.ArtistDisplay;
        var album = string.IsNullOrWhiteSpace(track.Album) ? null : track.Album;
        return (artist.Length > 0, album) switch
        {
            (true, { } a) => Strings.Format("notification.track.body", ("artist", artist), ("album", a)),
            (true, null) => artist,
            (false, { } a) => a,
            _ => "",
        };
    }

    public void Dispose()
    {
        _coordinator.TrackStarted -= OnTrackStarted;
        _coordinator.NowPlayingChanged -= OnNowPlayingChanged;
        _notifier.Dispose();
    }
}

/// <summary>
/// The notification's copy of a cover. Windows reads a toast's image by its
/// extension and keeps it by its URI, so each copy gets the extension of its
/// format and a name of its own; the copies before it are deleted.
/// </summary>
public static class NotificationCover
{
    /// <summary>The copy's path; null when the format isn't one both platforms
    /// show (PNG, JPEG, GIF) or the file can't be read or copied.</summary>
    public static string? Copy(string source, string directory)
    {
        try
        {
            if (Extension(source) is not { } extension)
                return null;
            Directory.CreateDirectory(directory);
            foreach (var old in Directory.EnumerateFiles(directory))
            {
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            var copy = Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
            File.Copy(source, copy);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("notify", $"couldn't copy the cover: {ex.Message}");
            return null;
        }
    }

    internal static string? Extension(string path)
    {
        Span<byte> head = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        head = head[..read];
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']))
            return ".png";
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return ".jpg";
        if (head.StartsWith("GIF8"u8))
            return ".gif";
        return null;
    }
}
