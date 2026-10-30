using Rambutan.Core.Models;
using Rambutan.Core.Stats;

namespace Rambutan.Core.Playback;

/// <summary>
/// Decides when a play goes to the account's YouTube Music history: once per
/// play, when it counts as a play in the stats (<see cref="PlayRules.Threshold"/>,
/// from audio actually heard), and only for a YouTube id, so a local file is
/// never reported. Pure: playback events and an injected clock in, at most one
/// video id per play out.
/// </summary>
/// <remarks>Every <see cref="OnNowPlaying"/> is a new play, so a repeat-one
/// replay is reported again. Not thread-safe; the listener hub calls one event at
/// a time.</remarks>
public sealed class HistoryReportGate(TimeProvider clock)
{
    private readonly ListenMeter _meter = new(clock);
    private string? _videoId;
    private TimeSpan? _duration;
    private bool _reported;

    public void OnNowPlaying(TrackInfo? track)
    {
        _videoId = track is not null && YouTubeIds.IsVideoId(track.VideoId) ? track.VideoId : null;
        _duration = track?.Duration;
        _reported = false;
        _meter.Restart();
    }

    public void OnStateChanged(PlaybackState state) => _meter.OnStateChanged(state);

    /// <summary>The video id to report, exactly once per play, on the step that earns it.</summary>
    public string? OnProgress(TimeSpan position, TimeSpan? duration)
    {
        if (duration is { } known && known > TimeSpan.Zero)
            _duration = known;
        _meter.OnProgress(position);

        if (_videoId is null || _reported || _meter.Listened <= TimeSpan.Zero
            || _meter.Listened < PlayRules.Threshold(_duration))
            return null;

        _reported = true;
        return _videoId;
    }
}
