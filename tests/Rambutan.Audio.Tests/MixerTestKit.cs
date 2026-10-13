namespace Rambutan.Audio.Tests;

/// <summary>
/// Synthetic tracks and a sample clock for driving <see cref="CrossfadeMixer"/>
/// without a decoder or a device: each <see cref="MixFrames"/> call is one
/// device callback, so time advances only when the test says so.
/// </summary>
internal static class MixerTestKit
{
    public const int Channels = FfmpegDecoder.Channels;
    public const int Rate = FfmpegDecoder.SampleRate;

    /// <summary>One device callback of 10 ms.</summary>
    public const int CallbackFrames = Rate / 100;

    /// <summary>A track whose every sample is <paramref name="value"/>.</summary>
    public static BufferedTrack Dc(string name, float value, double seconds, bool complete = true)
    {
        var samples = new float[(int)(seconds * Rate) * Channels];
        Array.Fill(samples, value);
        return new BufferedTrack(name, samples, complete);
    }

    /// <summary>
    /// A track whose frame n carries the value (n + 1) × <see cref="FrameStep"/> on
    /// both channels, so an output sample says which frame of the track it is.
    /// </summary>
    public static BufferedTrack Numbered(string name, double seconds)
    {
        var frames = (int)(seconds * Rate);
        var samples = new float[frames * Channels];
        for (var n = 0; n < frames; n++)
            samples[n * Channels] = samples[n * Channels + 1] = (n + 1) * FrameStep;
        return new BufferedTrack(name, samples);
    }

    public const float FrameStep = 1e-5f;

    /// <summary>Runs callbacks for <paramref name="seconds"/> and returns every sample produced.</summary>
    public static float[] Run(CrossfadeMixer mixer, double seconds)
    {
        var callbacks = (int)Math.Round(seconds * Rate / CallbackFrames);
        var all = new float[callbacks * CallbackFrames * Channels];
        for (var i = 0; i < callbacks; i++)
            mixer.Mix(all.AsSpan(i * CallbackFrames * Channels, CallbackFrames * Channels));
        return all;
    }

    /// <summary>The left channel of each frame.</summary>
    public static float[] Left(float[] samples)
    {
        var left = new float[samples.Length / Channels];
        for (var i = 0; i < left.Length; i++)
            left[i] = samples[i * Channels];
        return left;
    }
}
