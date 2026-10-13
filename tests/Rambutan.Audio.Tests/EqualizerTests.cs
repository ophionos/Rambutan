namespace Rambutan.Audio.Tests;

public class EqualizerTests
{
    private const int Rate = FfmpegDecoder.SampleRate;
    private const int Block = 1024; // frames per device callback, roughly

    private static double[] Flat => new double[Equalizer.Frequencies.Count];

    private static double[] Gains(int band, double db)
    {
        var gains = Flat;
        gains[band] = db;
        return gains;
    }

    /// <summary>Interleaved stereo sine, both channels alike.</summary>
    private static float[] Sine(double hz, double seconds, float amplitude = 0.5f)
    {
        var frames = (int)(Rate * seconds);
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
            samples[2 * i] = samples[2 * i + 1] = amplitude * (float)Math.Sin(2 * Math.PI * hz * i / Rate);
        return samples;
    }

    /// <summary>Runs the samples through in device-sized blocks.</summary>
    private static float[] Run(Equalizer eq, float[] input)
    {
        var output = (float[])input.Clone();
        for (var start = 0; start < output.Length; start += Block * 2)
            eq.Process(output.AsSpan(start, Math.Min(Block * 2, output.Length - start)));
        return output;
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples)
            sum += s * (double)s;
        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>The level of the last half second against the input's, in dB:
    /// past the transient, which the 31 Hz band needs about that long to settle.</summary>
    private static double GainDb(float[] input, float[] output)
    {
        var tail = Rate; // samples: half a second of stereo
        return 20 * Math.Log10(Rms(output.AsSpan(^tail)) / Rms(input.AsSpan(^tail)));
    }

    [Fact]
    public void Off_or_flat_leaves_every_sample_as_it_was()
    {
        var input = Sine(440, 0.2);
        var off = new Equalizer();
        off.Set(false, Gains(5, 6));
        var flat = new Equalizer();
        flat.Set(true, Flat);

        Assert.Equal(input, Run(off, input));
        Assert.Equal(input, Run(flat, input));
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(1, -9)]
    [InlineData(5, 12)]
    [InlineData(7, -12)]
    [InlineData(9, 4)]
    public void A_band_moves_its_centre_by_its_gain_less_the_headroom(int band, double db)
    {
        var eq = new Equalizer();
        var gains = Gains(band, db);
        eq.Set(true, Flat);    // on, so the change below isn't the switch's crossfade
        Run(eq, Sine(1000, 0.05));
        eq.Set(true, gains);
        var input = Sine(Equalizer.Frequencies[band], 1.5);

        var measured = GainDb(input, Run(eq, input));

        var expected = db + Equalizer.HeadroomDb(gains);
        Assert.InRange(measured, expected - 0.5, expected + 0.5);
    }

    [Fact]
    public void A_cut_takes_no_headroom_and_a_boost_takes_its_peak()
    {
        Assert.Equal(0, Equalizer.HeadroomDb(Gains(3, -8)));
        Assert.InRange(Equalizer.HeadroomDb(Gains(3, 8)), -8.05, -7.95);
        // Neighbouring boosts overlap, so their peak is above either gain.
        var both = Flat;
        both[4] = both[5] = 6;
        Assert.True(Equalizer.HeadroomDb(both) < -6.5);
    }

    [Fact]
    public void Every_band_at_full_boost_never_goes_past_full_scale()
    {
        var all = Flat.Select(_ => Equalizer.MaxGainDb).ToArray();
        var headroom = Equalizer.HeadroomDb(all);

        for (var hz = 20.0; hz < 20000; hz *= 1.05)
            Assert.True(Equalizer.ResponseDb(all, hz) + headroom <= 0.01, $"{hz:F0} Hz is over full scale.");

        // And through the filters themselves: a full-scale tone stays within it.
        var eq = new Equalizer();
        eq.Set(true, all);
        foreach (var hz in new[] { 45.0, 700, 3000, 11000 })
        {
            var output = Run(eq, Sine(hz, 1.0, amplitude: 1f));
            var peak = output.AsSpan(Rate).ToArray().Max(Math.Abs);
            Assert.True(peak <= 1.01f, $"{hz} Hz peaked at {peak}.");
        }
    }

    [Fact]
    public void Gains_are_clamped_and_nonsense_is_flat()
    {
        var gains = Flat;
        gains[0] = 40;
        gains[1] = -40;
        gains[2] = double.NaN;

        var normalized = Equalizer.Normalize(gains);

        Assert.Equal([12.0, -12.0, 0.0], normalized.Take(3));
        Assert.Equal(Flat, Equalizer.Normalize([3, 3]));
    }

    [Fact]
    public void Stored_bands_are_matched_by_frequency()
    {
        var stored = Equalizer.Frequencies.Select((hz, i) => (hz, (double)i)).Reverse().ToList();

        Assert.Equal(Enumerable.Range(0, 10).Select(i => (double)i), Equalizer.FromBands(stored));
        Assert.Equal(Flat, Equalizer.FromBands(null));
        Assert.Equal(Flat, Equalizer.FromBands(stored.Take(9)));
        // A band the layout doesn't have makes the whole curve flat, not a guess.
        var moved = stored.Select(b => b.hz == 63 ? (60, b.Item2) : b);
        Assert.Equal(Flat, Equalizer.FromBands(moved));
    }

    [Fact]
    public void Switching_on_and_off_has_no_step()
    {
        var eq = new Equalizer();
        eq.Set(false, Gains(0, 12)); // on, the 1 kHz tone drops by the 12 dB of headroom
        var input = Sine(1000, 0.3);
        var output = (float[])input.Clone();

        // Off for a block, on for some, off again: neighbouring samples never jump
        // by more than the tone itself moves between them.
        var maxStep = 2 * Math.PI * 1000 / Rate * 0.5 * 4; // generous: 4x the tone's slope
        for (var block = 0; block * Block * 2 < output.Length; block++)
        {
            eq.Set(block is >= 2 and < 8, Gains(0, 12));
            var start = block * Block * 2;
            eq.Process(output.AsSpan(start, Math.Min(Block * 2, output.Length - start)));
        }

        for (var i = 2; i < output.Length; i += 2)
            Assert.True(Math.Abs(output[i] - output[i - 2]) <= maxStep, $"Step at frame {i / 2}.");
    }

    [Fact]
    public void Silence_after_sound_settles_on_exact_zero()
    {
        var eq = new Equalizer();
        eq.Set(true, Gains(0, 12));
        Run(eq, Sine(31, 0.5));

        var silence = Run(eq, new float[Rate * 2 * 20]);

        // The ringing dies away and the state is flushed, not left in subnormals.
        Assert.All(silence.AsSpan(^(Rate * 2)).ToArray(), s => Assert.Equal(0f, s));
    }
}
