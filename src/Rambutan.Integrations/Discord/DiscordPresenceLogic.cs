using System.Globalization;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.Integrations.Discord;

/// <summary>What the user lets Rambutan show on Discord (per-element toggles).</summary>
public sealed record DiscordPresenceOptions(bool ShowTrack = true, bool ShowArtwork = true, bool ShowElapsed = true);

public enum DiscordConnectionState
{
    /// <summary>The integration is turned off (or unavailable in this build).</summary>
    Off,

    /// <summary>On, but no Discord client answered (or it refused the handshake).</summary>
    Waiting,

    Connected,
}

/// <summary>
/// Follows playback and says what presence Discord should show right now. Pure:
/// every call takes the current time, so seek handling is unit-tested without a
/// clock or a socket.
/// </summary>
public sealed class DiscordPresenceTracker
{
    /// <summary>How far the implied start time may drift before it's re-sent:
    /// ordinary tick jitter stays below it, a seek doesn't.</summary>
    public static readonly TimeSpan SeekTolerance = TimeSpan.FromSeconds(2);

    /// <summary>Asset key of the logo uploaded to the Discord application.</summary>
    public const string FallbackImageKey = "rambutan";

    // Discord rejects strings shorter than 2 or longer than 128 characters, and
    // image keys (including URLs) longer than 256.
    public const int MaxText = 128;
    public const int MaxImageKey = 256;

    private TrackInfo? _track;
    private PlaybackState _state = PlaybackState.Stopped;
    private TimeSpan _position;
    private TimeSpan? _duration;
    private DateTimeOffset? _start;

    public void OnNowPlaying(TrackInfo? track)
    {
        _track = track;
        _position = TimeSpan.Zero;
        _duration = track?.Duration;
        _start = null;
    }

    public void OnState(PlaybackState state, DateTimeOffset now)
    {
        _state = state;
        if (IsRunning(state))
            Anchor(now, force: _start is null);
        else
            _start = null; // the clock stopped; resuming re-anchors from the position
    }

    public void OnProgress(TimeSpan position, TimeSpan? duration, DateTimeOffset now)
    {
        _position = position;
        if (duration is { } d && d > TimeSpan.Zero)
            _duration = d;
        if (IsRunning(_state))
            Anchor(now, force: false);
    }

    /// <summary>The activity to show at <paramref name="now"/>; null means clear it.</summary>
    public DiscordActivity? Desired(DateTimeOffset now, DiscordPresenceOptions options)
    {
        if (_track is not { } track)
            return null;
        // Paused clears too: Discord has no paused state, and an activity without
        // timestamps gets Discord's own clock counting up from when it arrived,
        // which reads as playing.
        if (_state is PlaybackState.Stopped or PlaybackState.Faulted or PlaybackState.Paused)
            return null;

        DiscordTimestamps? timestamps = null;
        if (options.ShowElapsed && IsRunning(_state) && _start is { } start)
        {
            var startMs = start.ToUnixTimeMilliseconds();
            long? endMs = _duration is { } d && d > TimeSpan.Zero
                ? startMs + (long)d.TotalMilliseconds
                : null;
            timestamps = new DiscordTimestamps(startMs, endMs);
        }

        return new DiscordActivity
        {
            Details = options.ShowTrack ? Fit(track.Title) : null,
            State = options.ShowTrack ? Fit(track.ArtistDisplay) : null,
            Timestamps = timestamps,
            Assets = new DiscordAssets(
                options.ShowArtwork ? ImageKey(track.ThumbnailUrl) : FallbackImageKey,
                options.ShowTrack ? Fit(track.Album) : null),
        };
    }

    /// <summary>An https artwork URL Discord will proxy, else the uploaded logo.</summary>
    public static string ImageKey(string? thumbnailUrl) =>
        thumbnailUrl is { Length: <= MaxImageKey } url
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
            ? url
            : FallbackImageKey;

    /// <summary>Trims to Discord's 2–128 character window; null when nothing is left.</summary>
    public static string? Fit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        if (text.Length > MaxText)
        {
            // Cut on a text-element boundary so an emoji or surrogate pair survives.
            var info = new StringInfo(text);
            var cut = Math.Min(info.LengthInTextElements, MaxText);
            string shortened;
            do
            {
                shortened = info.SubstringByTextElements(0, --cut);
            }
            while (shortened.Length > MaxText - 1);
            text = shortened + "…";
        }

        // One-character titles exist ("X"); a braille blank pads them without
        // showing anything, the usual workaround for the 2-character minimum.
        return text.Length < 2 ? text + "⠀" : text;
    }

    private void Anchor(DateTimeOffset now, bool force)
    {
        var implied = now - _position;
        if (force || _start is not { } start || (implied - start).Duration() > SeekTolerance)
            _start = implied;
    }

    private static bool IsRunning(PlaybackState state) =>
        state is PlaybackState.Playing or PlaybackState.Buffering;
}

/// <summary>
/// Reconnect delays while Discord isn't running: quick at first (Discord was just
/// starting), then settling at one attempt a minute.
/// </summary>
public sealed class DiscordReconnectBackoff
{
    public static readonly IReadOnlyList<TimeSpan> Schedule =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private int _attempt;

    public TimeSpan Next() => Schedule[Math.Min(_attempt++, Schedule.Count - 1)];

    public void Reset() => _attempt = 0;
}

/// <summary>Discord accepts five SET_ACTIVITY calls per twenty seconds; later ones
/// wait for a slot rather than being dropped (the latest state wins).</summary>
public sealed class DiscordSendBudget
{
    public const int Limit = 5;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    private readonly Queue<DateTimeOffset> _sent = new();

    /// <summary>Zero when a send may go now, else how long until a slot frees.</summary>
    public TimeSpan WaitBefore(DateTimeOffset now)
    {
        while (_sent.Count > 0 && now - _sent.Peek() >= Window)
            _sent.Dequeue();
        return _sent.Count < Limit ? TimeSpan.Zero : _sent.Peek() + Window - now;
    }

    public void Record(DateTimeOffset now) => _sent.Enqueue(now);

    public void Reset() => _sent.Clear();
}
