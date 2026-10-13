using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Downloads;
using Rambutan.Core.Models;
using Rambutan.Core.Settings;
using Rambutan.Streams;

namespace Rambutan.App.Services.Demo;

/// <summary>
/// "Downloads" a track as the demo's generated audio, paced so progress is
/// visible: about four seconds a track, plus <see cref="DemoOptions.Latency"/>
/// before it starts. With <c>RAMBUTAN_DEMO_FAIL=downloads</c> every download fails
/// halfway, so the failed state and Retry can be looked at.
/// </summary>
public sealed class DemoAudioFetcher(DemoOptions? options = null) : IAudioFetcher
{
    private static readonly TimeSpan Pace = TimeSpan.FromSeconds(4);

    public Task<FetchedAudio> FetchAsync(
        TrackInfo track, Stream target, IProgress<DownloadProgress> progress, CancellationToken ct) =>
        Task.Run(async () =>
        {
            if (options?.Latency is { } latency && latency > TimeSpan.Zero)
                await Task.Delay(latency, ct);

            var duration = DemoStreamSource.DurationOf(track);
            long before = 0;
            DemoStreamSource.WriteTrack(target, track.VideoId, duration, (written, total) =>
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new DownloadProgress(written, total));
                if (options?.Fails("downloads") == true && written * 2 > total)
                    throw new IOException("demo: the download failed halfway (RAMBUTAN_DEMO_FAIL=downloads)");
                // Spread the whole track over Pace, a cycle's share at a time.
                Thread.Sleep(Pace * ((double)(written - before) / total));
                before = written;
            });
            return new FetchedAudio("pcm_u8", Itag: null, AudioQuality.High, LoudnessDb: null);
        }, ct);
}
