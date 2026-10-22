using System.Text;
using Rambutan.Core.Links;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.Integrations.Linux;

/// <summary>
/// What the MPRIS player object reports, kept apart from D-Bus so the rules can
/// be tested anywhere: the playback status, the track metadata, where playback
/// is now, the volume, shuffle and loop, and when a position jump is a seek the clients must be
/// told about.
/// Not thread-safe; <see cref="MprisMediaSessionService"/> locks around it.
/// </summary>
public sealed class MprisPlayerState(TimeProvider clock)
{
    /// <summary>The object-path prefix for track ids (the spec keeps
    /// /org/mpris for its own use).</summary>
    public const string TrackIdPrefix = "/app/rambutan/track/";

    /// <summary>The id of "no track", as the spec defines it.</summary>
    public const string NoTrack = "/org/mpris/MediaPlayer2/TrackList/NoTrack";

    /// <summary>A reported position this far from where playback should be is
    /// a seek. Progress arrives several times a second, so drift stays far below.</summary>
    private static readonly TimeSpan SeekThreshold = TimeSpan.FromSeconds(1.5);

    private TimeSpan _position;
    private long _positionAt;

    public TrackInfo? Track { get; private set; }

    public SessionPlaybackStatus Status { get; private set; }

    public bool IsPlaying => Status == SessionPlaybackStatus.Playing;

    /// <summary>0 to 1.</summary>
    public double Volume { get; private set; } = 1.0;

    public bool Shuffle { get; private set; }

    public RepeatMode Repeat { get; private set; }

    /// <summary>The repeat mode in MPRIS's words.</summary>
    public string LoopStatus => ToLoopStatus(Repeat);

    /// <summary>The track's length: the timeline's when it has one, else the catalog's.</summary>
    public TimeSpan? Duration { get; private set; }

    public string PlaybackStatus => Track is null ? "Stopped" : Status switch
    {
        SessionPlaybackStatus.Playing => "Playing",
        SessionPlaybackStatus.Paused => "Paused",
        _ => "Stopped",
    };

    public string TrackId => Track is null ? NoTrack : TrackIdFor(Track.VideoId);

    public bool HasTrack => Track is not null;

    public bool CanSeek => Track is not null && Duration is not null;

    /// <summary>Where playback is now: the last reported position, moved on by
    /// the time since while playing, and never past the end. Stopped is the
    /// start, where Play begins.</summary>
    public TimeSpan Position
    {
        get
        {
            if (Track is null || Status == SessionPlaybackStatus.Stopped)
                return TimeSpan.Zero;
            var position = IsPlaying ? _position + clock.GetElapsedTime(_positionAt) : _position;
            return Duration is { } d && position > d ? d : position;
        }
    }

    /// <summary>A new track (or none). True when anything reported changed.</summary>
    public bool SetTrack(TrackInfo? track)
    {
        if (track == Track)
            return false;
        Track = track;
        Duration = track?.Duration;
        MoveTo(TimeSpan.Zero);
        if (track is null)
            Status = SessionPlaybackStatus.Stopped;
        return true;
    }

    /// <summary>True when the status changed.</summary>
    public bool SetStatus(SessionPlaybackStatus status)
    {
        if (status == Status)
            return false;
        // Freeze or restart the clock at the position reached so far.
        MoveTo(Position);
        Status = status;
        return true;
    }

    /// <summary>True when the volume changed. Kept within 0 to 1.</summary>
    public bool SetVolume(double volume)
    {
        volume = Math.Clamp(volume, 0.0, 1.0);
        if (double.IsNaN(volume) || volume == Volume)
            return false;
        Volume = volume;
        return true;
    }

    /// <summary>True when the shuffle mode changed.</summary>
    public bool SetShuffle(bool shuffle)
    {
        if (shuffle == Shuffle)
            return false;
        Shuffle = shuffle;
        return true;
    }

    /// <summary>True when the repeat mode changed.</summary>
    public bool SetRepeat(RepeatMode repeat)
    {
        if (repeat == Repeat)
            return false;
        Repeat = repeat;
        return true;
    }

    /// <summary>MPRIS's LoopStatus for a repeat mode: None, Track or Playlist.</summary>
    public static string ToLoopStatus(RepeatMode repeat) => repeat switch
    {
        RepeatMode.One => "Track",
        RepeatMode.All => "Playlist",
        _ => "None",
    };

    /// <summary>The repeat mode a LoopStatus names; null for anything the spec
    /// doesn't define (the names are case-sensitive).</summary>
    public static RepeatMode? FromLoopStatus(string loopStatus) => loopStatus switch
    {
        "None" => RepeatMode.Off,
        "Track" => RepeatMode.One,
        "Playlist" => RepeatMode.All,
        _ => null,
    };

    /// <summary>
    /// A progress report. <paramref name="seeked"/> is set when the position
    /// jumped (a seek, from here or anywhere else); <paramref name="durationChanged"/>
    /// when the length became known or changed, which changes the metadata.
    /// </summary>
    public void SetTimeline(TimeSpan position, TimeSpan? duration, out bool seeked, out bool durationChanged)
    {
        seeked = Track is not null && Status != SessionPlaybackStatus.Stopped
            && (position - Position).Duration() > SeekThreshold;
        durationChanged = duration is not null && duration != Duration;
        if (durationChanged)
            Duration = duration;
        MoveTo(position);
    }

    private void MoveTo(TimeSpan position)
    {
        _position = position;
        _positionAt = clock.GetTimestamp();
    }

    /// <summary>
    /// The object path that names a track. A videoId may hold '-', which an
    /// object path can't, so the id is spelled out in hex.
    /// </summary>
    public static string TrackIdFor(string videoId) =>
        TrackIdPrefix + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(videoId));

    /// <summary>The metadata entries for the current track (none without one).</summary>
    public IReadOnlyList<MprisMetadataEntry> Metadata()
    {
        if (Track is not { } track)
            return [new("mpris:trackid", MprisValue.OfPath(NoTrack))];

        var entries = new List<MprisMetadataEntry>
        {
            new("mpris:trackid", MprisValue.OfPath(TrackId)),
            new("xesam:title", MprisValue.OfText(track.Title)),
            new("xesam:artist", MprisValue.OfTextList([.. track.Artists.Select(a => a.Name)])),
        };
        if (ShareLinks.ForTrack(track) is { } url)
            entries.Add(new("xesam:url", MprisValue.OfText(url)));
        if (!string.IsNullOrWhiteSpace(track.Album))
            entries.Add(new("xesam:album", MprisValue.OfText(track.Album)));
        if (Duration is { } length)
            entries.Add(new("mpris:length", MprisValue.OfMicroseconds(length)));
        if (track.ThumbnailUrl is { } art && Uri.TryCreate(art, UriKind.Absolute, out _))
            entries.Add(new("mpris:artUrl", MprisValue.OfText(art)));
        return entries;
    }

    /// <summary>MPRIS times are whole microseconds in an int64.</summary>
    public static long ToMicroseconds(TimeSpan time) => time.Ticks / TimeSpan.TicksPerMicrosecond;

    public static TimeSpan FromMicroseconds(long microseconds) =>
        TimeSpan.FromTicks(microseconds * TimeSpan.TicksPerMicrosecond);
}

public sealed record MprisMetadataEntry(string Key, MprisValue Value);

/// <summary>A metadata value, in the D-Bus type the spec gives its key.</summary>
public sealed record MprisValue(char Kind, string? Text = null, IReadOnlyList<string>? List = null, long Number = 0)
{
    /// <summary>An object path ("o").</summary>
    public static MprisValue OfPath(string path) => new('o', Text: path);

    /// <summary>A string ("s").</summary>
    public static MprisValue OfText(string text) => new('s', Text: text);

    /// <summary>A string array ("as").</summary>
    public static MprisValue OfTextList(IReadOnlyList<string> list) => new('a', List: list);

    /// <summary>A time in microseconds ("x").</summary>
    public static MprisValue OfMicroseconds(TimeSpan time) => new('x', Number: MprisPlayerState.ToMicroseconds(time));
}
