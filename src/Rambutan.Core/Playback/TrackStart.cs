using Rambutan.Core.Models;

namespace Rambutan.Core.Playback;

/// <summary>A track began to play.</summary>
/// <param name="OnItsOwn">True when playback moved on without the user: the
/// last track ended, autoplay continued, or a failed track was skipped. False
/// for a play, skip or jump the user asked for, from any surface.</param>
public sealed record TrackStart(TrackInfo Track, bool OnItsOwn);
