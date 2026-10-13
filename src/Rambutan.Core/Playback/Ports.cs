using Rambutan.Core.Models;

namespace Rambutan.Core.Playback;

public interface IStreamSource
{
    /// <param name="bypassCache">Force re-resolution (used when a cached URL has expired).</param>
    Task<ResolvedMedia> ResolveAsync(string videoId, bool bypassCache = false, CancellationToken ct = default);
}

/// <summary>
/// Where autoplay continuation comes from: a radio seeded by a track, YTM's
/// "Start radio". <see cref="Catalog.ICatalog"/> is one.
/// </summary>
public interface IRadioSource
{
    /// <summary>
    /// Builds a radio queue seeded by a track (YTM's "Start radio" / automix).
    /// Works anonymously; a signed-in session personalizes the mix.
    /// </summary>
    Task<IReadOnlyList<TrackInfo>> GetRadioAsync(string videoId, CancellationToken ct = default);
}

/// <param name="LoudnessDb">Track loudness relative to the platform reference in
/// dB (positive = louder), when the source could recover it. Feeds volume
/// normalization.</param>
public sealed record ResolvedMedia(string Url, string AudioCodec, double? LoudnessDb = null);

/// <summary>
/// The player couldn't open the source at all (HTTP denial, dead URL, unreadable
/// file) — as opposed to a mid-decode failure. Signals the coordinator that a
/// fresh <see cref="IStreamSource"/> resolution may succeed where retrying the
/// same URL won't: googlevideo URLs are IP-bound and can die before their
/// nominal expiry.
/// </summary>
public sealed class StreamOpenException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// The source couldn't resolve a track because the network can't be reached.
/// Unlike other resolution failures, a fresh resolution won't help, and neither
/// will the next track that needs the network: only a track that plays offline will.
/// </summary>
public sealed class OfflineException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// A source that can tell, without the network, which tracks it plays offline
/// (downloads, cached audio). When a track fails with <see cref="OfflineException"/>,
/// the coordinator uses it to skip straight to the next such track instead of
/// trying each one in between.
/// </summary>
public interface IOfflineAvailability
{
    bool IsAvailableOffline(string videoId);
}

/// <summary>
/// The audio engine as the coordinator sees it: a current slot and an optional
/// preloaded next slot, with equal-power crossfade between them. Events arrive on
/// engine threads — subscribers marshal.
/// </summary>
public interface IAudioPlayer
{
    /// <summary>Prebuffers and swaps in as the current track. Playback starts immediately.</summary>
    Task PlayAsync(AudioTrackRequest request, CancellationToken ct = default);

    /// <summary>Prebuffers and arms the gapless/crossfade successor.</summary>
    Task PreloadNextAsync(AudioTrackRequest request, CancellationToken ct = default);

    void ClearNext();

    /// <summary>Starts crossfading into the armed next track (no-op without one).</summary>
    void BeginCrossfade(TimeSpan duration);

    /// <summary>
    /// Restarts the current track at <paramref name="position"/> on a new decode
    /// chain. The armed next survives, unless a fade had started reading it: then
    /// the cut drops it, and <see cref="SeekResult.NextKept"/> is false so the
    /// caller can arm it again from its start.
    /// </summary>
    Task<SeekResult> SeekAsync(TimeSpan position, CancellationToken ct = default);

    void Pause();
    void Resume();
    void Stop();

    /// <summary>Master gain, 0..1.</summary>
    double Volume { get; set; }

    event Action<AudioPlayerEvent>? EventRaised;
}

/// <param name="Key">Opaque identity minted by the coordinator; echoed back in events.</param>
/// <param name="LoudnessDb">Normalization input: the engine attenuates
/// tracks louder than reference; null plays at unity gain.</param>
public sealed record AudioTrackRequest(
    string Key, string Url, string DisplayName, TimeSpan? Duration, TimeSpan StartAt,
    double? LoudnessDb = null);

public abstract record AudioPlayerEvent;

/// <param name="IsStarved">Ring buffer empty while the track hasn't ended (network stall).</param>
/// <param name="Chain">The decode chain the position was read from. A seek keeps the
/// key but replaces the chain, so a tick read just before the swap can arrive after
/// the seek; the chain tells it apart.</param>
public sealed record ProgressEvent(
    string Key, TimeSpan Position, TimeSpan Buffered, TimeSpan? Duration, bool IsStarved,
    long Chain = 0) : AudioPlayerEvent;

/// <param name="NextKept">False when the cut dropped an armed next track that a fade
/// had started reading.</param>
/// <param name="Chain">The decode chain that now plays the track, as later
/// <see cref="ProgressEvent.Chain"/> values name it; null when the seek didn't land
/// (the track had already moved on).</param>
public sealed record SeekResult(bool NextKept, long? Chain);

/// <summary>The armed next track took over (gapless handover or crossfade completion).</summary>
public sealed record TrackPromotedEvent(string Key) : AudioPlayerEvent;

/// <summary>The current track drained with no successor armed.</summary>
public sealed record PlaybackEndedEvent(string Key) : AudioPlayerEvent;

public sealed record PlayerFaultedEvent(string Key, string Reason) : AudioPlayerEvent;

/// <summary>Output moved to another device (unplug recovery, or a null/silent
/// backend announced at start) and playback continues on it.</summary>
public sealed record DeviceChangedEvent(string DeviceName) : AudioPlayerEvent;

/// <summary>No output device could be opened; playback is stalled until one
/// returns. Surfaced, never silent.</summary>
public sealed record DeviceLostEvent : AudioPlayerEvent;

public sealed record PlaybackProgress(TimeSpan Position, TimeSpan? Duration, TimeSpan Buffered);
