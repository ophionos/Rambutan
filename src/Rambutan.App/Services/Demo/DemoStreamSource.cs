using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;

namespace Rambutan.App.Services.Demo;

/// <summary>
/// Resolves every track to generated audio on disk, so the real engine (ffmpeg
/// decode, crossfade mixer, FFT analyzer, device output) plays something without
/// googlevideo. Each track is a short chord loop in its own key and tempo, as long
/// as the track claims to be, so the seek bar, crossfade timing and visualizer all
/// behave as they would with real audio. It's mono 8-bit 16 kHz: about 1 MB a minute,
/// and the directory is cleared each launch.
/// </summary>
public sealed class DemoStreamSource : IStreamSource, IStreamWarmer, IOfflineAvailability
{
    // Hover warm-up has nothing to resolve here: the audio is generated on
    // play, from the queued track's length. Accepting it keeps the trigger visible
    // in the demo's log.
    public bool IsWarm(string videoId) => false;

    public Task WarmAsync(string videoId, CancellationToken ct) => Task.CompletedTask;

    private const int SampleRate = 16_000;
    private static readonly TimeSpan FallbackDuration = TimeSpan.FromMinutes(3);

    private readonly QueueModel _queue;
    private readonly string _dir;
    private readonly bool _offline;
    private long _offlineUntilTicks;

    /// <param name="offline">Every resolution fails as the real source's does with
    /// no network (downloads still play: they sit in front of this).</param>
    public DemoStreamSource(QueueModel queue, string directory, bool offline = false)
    {
        _queue = queue;
        _dir = directory;
        _offline = offline;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Fails every resolution for <paramref name="outage"/> from now, as
    /// a network that went away and comes back (<see cref="DemoNetworkDrop"/>).</summary>
    public void DropNetwork(TimeSpan outage) =>
        Interlocked.Exchange(ref _offlineUntilTicks, (DateTime.UtcNow + outage).Ticks);

    // Generated audio isn't kept between launches, so nothing here counts as offline.
    public bool IsAvailableOffline(string videoId) => false;

    public Task<ResolvedMedia> ResolveAsync(
        string videoId, bool bypassCache = false, CancellationToken ct = default)
    {
        if (_offline || DateTime.UtcNow.Ticks < Interlocked.Read(ref _offlineUntilTicks))
            return Task.FromException<ResolvedMedia>(
                new OfflineException($"couldn't resolve {videoId}: demo streams are offline"));

        // The port only carries the id; the queue knows how long the track is.
        var duration = _queue.Items.FirstOrDefault(t => t.VideoId == videoId)?.Duration
                       ?? FallbackDuration;
        var path = Path.Combine(_dir, $"{string.Concat(videoId.Where(char.IsLetterOrDigit))}.wav");

        return Task.Run(() =>
        {
            if (!File.Exists(path))
                WriteTrack(path, videoId, duration);
            return new ResolvedMedia(path, "pcm_u8");
        }, ct);
    }

    private static void WriteTrack(string path, string seed, TimeSpan duration)
    {
        var temp = path + ".tmp";
        using (var file = File.Create(temp))
            WriteTrack(file, seed, duration);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The track as a WAV file into <paramref name="target"/>, a cycle at
    /// a time; <paramref name="onCycle"/> runs after each, with the bytes so far
    /// and the total (the demo downloader paces itself on it).</summary>
    internal static void WriteTrack(
        Stream target, string seed, TimeSpan duration, Action<long, long>? onCycle = null)
    {
        var hash = seed.Aggregate(17, (h, c) => unchecked(h * 31 + c));
        var random = new Random(hash);
        var root = 110.0 * Math.Pow(2, random.Next(0, 12) / 12.0);   // A2 … G#3
        var beat = 60.0 / random.Next(92, 132);                        // seconds per beat

        // The music repeats every 16 beats, so synthesise one cycle and tile it:
        // a five-minute track costs one eight-second synthesis, not five minutes of it.
        var cycle = Cycle(root, beat, random);
        var samples = (int)(Math.Max(1, duration.TotalSeconds) * SampleRate);
        var fadeSamples = (int)(1.5 * SampleRate);

        using (var file = new BinaryWriter(target, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WriteHeader(file, samples);
            var buffer = new byte[cycle.Length];
            for (var start = 0; start < samples; start += buffer.Length)
            {
                var count = Math.Min(buffer.Length, samples - start);
                for (var i = 0; i < count; i++)
                {
                    var n = start + i;
                    var fade = Math.Min(1.0, Math.Min(n, samples - n) / (double)fadeSamples);
                    buffer[i] = (byte)Math.Clamp(128 + cycle[i] * fade * 127, 0, 255);
                }
                file.Write(buffer, 0, count);
                file.Flush();
                onCycle?.Invoke(44L + start + count, 44L + samples);
            }
        }
    }

    /// <summary>One 16-beat cycle: I–V–vi–IV, an arpeggio over a bass note per beat,
    /// and a short noise burst on each off-beat for the visualizer's high bands.</summary>
    private static float[] Cycle(double root, double beat, Random random)
    {
        int[][] progression = [[0, 4, 7], [7, 11, 14], [9, 12, 16], [5, 9, 12]];
        var cycle = new float[(int)(16 * beat * SampleRate)];
        for (var i = 0; i < cycle.Length; i++)
        {
            var t = (double)i / SampleRate;
            var beatIndex = (int)(t / beat);
            var inBeat = t - beatIndex * beat;
            var chord = progression[beatIndex / 4 % progression.Length];

            var step = (int)(inBeat / (beat / 2));
            var note = chord[(beatIndex * 2 + step) % chord.Length];
            var inNote = inBeat - step * beat / 2;
            var lead = Math.Sin(2 * Math.PI * root * 2 * Semitones(note) * t)
                       * Math.Exp(-inNote * 6) * 0.35;
            var bass = Math.Sin(2 * Math.PI * root / 2 * Semitones(chord[0]) * t)
                       * Math.Exp(-inBeat * 3) * 0.3;
            var hat = inBeat > beat / 2 && inBeat < beat / 2 + 0.03
                ? (random.NextDouble() * 2 - 1) * 0.12 : 0;

            cycle[i] = (float)(lead + bass + hat);
        }
        return cycle;
    }

    internal static TimeSpan DurationOf(Core.Models.TrackInfo track) => track.Duration ?? FallbackDuration;

    private static double Semitones(int n) => Math.Pow(2, n / 12.0);

    private static void WriteHeader(BinaryWriter w, int samples)
    {
        w.Write("RIFF"u8); w.Write(36 + samples); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(SampleRate); w.Write(SampleRate); w.Write((short)1); w.Write((short)8);
        w.Write("data"u8); w.Write(samples);
    }
}
