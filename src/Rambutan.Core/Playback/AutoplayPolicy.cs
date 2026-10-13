using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.Core.Playback;

public static class AutoplayPolicy
{
    /// <summary>
    /// A queue that runs out continues with radio unless the user turned autoplay off
    /// or repeat is on. Repeat all never runs out, and repeat one is a request to hear
    /// the same thing again, so neither is topped up with something new.
    /// </summary>
    public static bool ShouldContinue(bool autoplayEnabled, RepeatMode repeat) =>
        autoplayEnabled && repeat == RepeatMode.Off;

    /// <summary>
    /// Picks the tracks to append from a radio seeded by <paramref name="seed"/>, in
    /// the radio's order: never the seed itself (a radio leads with it), never a track
    /// already queued, never the same track twice. Tracks already played this session
    /// are left out too, unless that would leave nothing, in which case they stay: a
    /// repeat is better than the queue ending when the radio had songs to offer.
    /// </summary>
    public static IReadOnlyList<TrackInfo> SelectContinuation(
        TrackInfo seed, IReadOnlyList<TrackInfo> radio,
        IReadOnlySet<string> queued, IReadOnlySet<string> playedThisSession)
    {
        var seen = new HashSet<string> { seed.VideoId };
        var candidates = new List<TrackInfo>();
        foreach (var track in radio)
        {
            if (string.IsNullOrEmpty(track.VideoId) || queued.Contains(track.VideoId) || !seen.Add(track.VideoId))
                continue;
            candidates.Add(track);
        }

        var fresh = candidates.Where(t => !playedThisSession.Contains(t.VideoId)).ToArray();
        return fresh.Length > 0 ? fresh : candidates;
    }
}
