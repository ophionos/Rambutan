using Rambutan.Core.Playback;
using Rambutan.Core.Queue;
using Rambutan.Core.Settings;

namespace Rambutan.App.Tests;

/// <summary>
/// A real <see cref="PlaybackCoordinator"/> over ports that resolve and "play"
/// instantly, for view models that read what is playing. Nothing is heard and no
/// player events are raised: a started track just stays current.
/// </summary>
internal static class FakePlayback
{
    public static PlaybackCoordinator Coordinator(QueueModel queue) =>
        new(new Streams(), new Player(), queue, new PlaybackSettings());

    private sealed class Streams : IStreamSource
    {
        public Task<ResolvedMedia> ResolveAsync(string videoId, bool bypassCache = false, CancellationToken ct = default) =>
            Task.FromResult(new ResolvedMedia($"https://cdn.test/{videoId}", "opus"));
    }

    private sealed class Player : IAudioPlayer
    {
        public double Volume { get; set; }

        public event Action<AudioPlayerEvent>? EventRaised { add { } remove { } }

        public Task PlayAsync(AudioTrackRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task PreloadNextAsync(AudioTrackRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public void ClearNext() { }
        public void BeginCrossfade(TimeSpan duration) { }
        public Task<SeekResult> SeekAsync(TimeSpan position, CancellationToken ct = default) => Task.FromResult(new SeekResult(true, null));
        public void Pause() { }
        public void Resume() { }
        public void Stop() { }
    }
}
