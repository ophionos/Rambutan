using Rambutan.Core.Models;

namespace Rambutan.Integrations.Taskbar;

/// <summary>The thumbnail toolbar's buttons, left to right. The value is the
/// button's id in the toolbar.</summary>
public enum TaskbarButton { Previous, PlayPause, Next, Like, Shuffle }

/// <summary>What a button draws. A thumbnail button has no checked state, so
/// Like and Shuffle carry theirs in the icon.</summary>
public enum TaskbarGlyph { Previous, Play, Pause, Next, Like, Liked, ShuffleOff, ShuffleOn }

/// <summary>One button as the toolbar should show it. <see cref="TipKey"/> is a
/// key of the app's string table; the tip is also the button's screen-reader name.</summary>
public readonly record struct TaskbarButtonView(
    TaskbarButton Button, TaskbarGlyph Glyph, string TipKey, bool IsEnabled, bool IsHidden);

/// <summary>Premultiplied BGRA pixels of a square icon, <see cref="Size"/> on a side.</summary>
public sealed record IconPixels(int Size, byte[] Bgra);

/// <summary>What the toolbar follows.</summary>
/// <param name="IsOn">The setting. Off hides every button: Windows lets an app
/// add buttons once and never remove them.</param>
/// <param name="HasQueue">Anything to play; transport is disabled without it, as in the player bar.</param>
/// <param name="IsPlaying">Playing or buffering, as the player bar's Pause shows.</param>
/// <param name="Track">The track the player bar shows: the loaded one, else the queue's current one.</param>
/// <param name="Rating">That track's rating as far as this session knows; null when unknown.</param>
public readonly record struct TaskbarInput(
    bool IsOn, bool HasQueue, bool IsPlaying, TrackInfo? Track, Rating? Rating,
    bool IsSignedIn, bool Shuffle);

/// <summary>The taskbar button's progress bar: <see cref="None"/>, or
/// <see cref="Completed"/> of <see cref="Total"/> milliseconds.</summary>
public readonly record struct TaskbarProgress(ulong Completed, ulong Total)
{
    public static TaskbarProgress None => default;

    public bool IsNone => Total == 0;
}

/// <summary>The rules for the Windows taskbar's thumbnail toolbar and progress
/// bar, kept apart from COM so they can be tested anywhere.</summary>
public static class TaskbarToolbarState
{
    public static TaskbarButtonView[] Buttons(TaskbarInput input)
    {
        var hidden = !input.IsOn;
        var liked = input.Rating == Rating.Like;
        return
        [
            new(TaskbarButton.Previous, TaskbarGlyph.Previous, "taskbar.previous", input.HasQueue, hidden),
            input.IsPlaying
                ? new(TaskbarButton.PlayPause, TaskbarGlyph.Pause, "taskbar.pause", input.HasQueue, hidden)
                : new(TaskbarButton.PlayPause, TaskbarGlyph.Play, "taskbar.play", input.HasQueue, hidden),
            new(TaskbarButton.Next, TaskbarGlyph.Next, "taskbar.next", input.HasQueue, hidden),
            // Signed out, or on a local file, a press could only fail; the tip says why it can't.
            new(TaskbarButton.Like, liked ? TaskbarGlyph.Liked : TaskbarGlyph.Like,
                !input.IsSignedIn ? "taskbar.likeSignedOut"
                    : input.Track is { } local && !YouTubeIds.IsVideoId(local.VideoId) ? "taskbar.likeLocal"
                    : liked ? "rating.removeLike" : "rating.like",
                input.Track is { } track && input.IsSignedIn && YouTubeIds.IsVideoId(track.VideoId), hidden),
            // A mode, so it can be set before there is a queue, as in the player bar.
            new(TaskbarButton.Shuffle, input.Shuffle ? TaskbarGlyph.ShuffleOn : TaskbarGlyph.ShuffleOff,
                input.Shuffle ? "player.shuffleOn" : "player.shuffleOff", true, hidden),
        ];
    }

    /// <summary>The bar for the <paramref name="loaded"/> track: always the normal
    /// state, paused or not (the paused and error states draw yellow and red, which
    /// read as warnings). Until playback reports a length, the track's listed one
    /// holds, so the bar doesn't blink off at each new track. None with the setting
    /// off, nothing loaded, or no known length.</summary>
    public static TaskbarProgress Progress(bool isOn, TrackInfo? loaded, TimeSpan position, TimeSpan? duration)
    {
        if (!isOn || loaded is null || (duration ?? loaded.Duration) is not { } length || length <= TimeSpan.Zero)
            return TaskbarProgress.None;
        var total = (ulong)Math.Max(1, (long)length.TotalMilliseconds);
        var done = (ulong)Math.Clamp((long)position.TotalMilliseconds, 0, (long)total);
        return new TaskbarProgress(done, total);
    }
}

/// <summary>
/// Passes on a progress value only when it differs visibly from the last one
/// passed on: progress arrives several times a second, and the bar needs about
/// one update a second. A seek, a new length or a change to or from
/// <see cref="TaskbarProgress.None"/> passes at once. Not thread-safe.
/// </summary>
public sealed class TaskbarProgressGate
{
    private const ulong Step = 1000;

    private TaskbarProgress? _last;

    /// <summary>The value to send, or null when the bar already shows it.</summary>
    public TaskbarProgress? Next(TaskbarProgress value)
    {
        if (_last is { } last && last.Total == value.Total &&
            (value.IsNone || Distance(last.Completed, value.Completed) < Step))
        {
            return null;
        }
        _last = value;
        return value;
    }

    private static ulong Distance(ulong a, ulong b) => a > b ? a - b : b - a;
}
