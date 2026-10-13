namespace Rambutan.Audio;

/// <summary>
/// Two-slot mixer: a current track plus an optional preloaded next track.
/// Crossfade is a real mixer stage — two decode chains overlapping with
/// equal-power gain curves, not a volume hack. When no crossfade is
/// requested, a finished current track hands over to the preloaded next track
/// inside the same device callback: sample-exact gapless.
/// </summary>
public sealed class CrossfadeMixer
{
    private readonly object _gate = new();
    private BufferedTrack? _current;
    private BufferedTrack? _next;

    private bool _crossfading;
    private bool _nextRead;           // a fade has consumed some of _next; it can't be rewound
    private long _fadeTotalSamples;   // per-channel sample count of the full fade
    private long _fadeElapsedSamples;

    // A rise to full gain still owed to the current track (the rest of its
    // fade-in after an early promotion, or its way back from a fade-out that was
    // called off): a linear ramp that reaches unity when the frames left run out.
    private double _rampStep;
    private long _rampFramesLeft;

    private float[] _nextScratch = [];
    private float _gain = 1f;
    private volatile float _targetGain = 1f;

    public event Action<BufferedTrack>? TrackStarted;

    /// <summary>When true, Mix outputs silence without consuming from the tracks.</summary>
    public volatile bool Paused;

    /// <summary>Master gain 0..1, ramped per frame in Mix to avoid zipper noise.</summary>
    public double MasterGain
    {
        get => _targetGain;
        set => _targetGain = (float)Math.Clamp(value, 0, 1);
    }

    public BufferedTrack? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>False once a cut mid-fade dropped the part-read next track.</summary>
    public bool HasNext
    {
        get { lock (_gate) return _next is not null; }
    }

    /// <summary>
    /// Cuts to <paramref name="track"/>. A next track that a fade has started
    /// reading is dropped: its ring can't be rewound, so a later handover would
    /// start it partway in. An unread next track stays armed.
    /// </summary>
    public void Play(BufferedTrack track)
    {
        lock (_gate)
            CutTo(track);
        TrackStarted?.Invoke(track);
    }

    /// <summary>
    /// <see cref="Play"/>, but only while <paramref name="expected"/> is still the
    /// current track: a seek's new decode chain prebuffers for a while, and a fade
    /// or a gapless handover can move on meanwhile. Returns false, changing
    /// nothing, when it did.
    /// </summary>
    public bool Replace(BufferedTrack expected, BufferedTrack replacement)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, expected))
                return false;
            CutTo(replacement);
        }
        TrackStarted?.Invoke(replacement);
        return true;
    }

    private void CutTo(BufferedTrack track)
    {
        _current?.Dispose();
        _current = track;
        if (_nextRead)
            DropNext();
        _crossfading = false;
        StopRamp();
    }

    public void SetNext(BufferedTrack track)
    {
        lock (_gate)
        {
            _next?.Dispose();
            _next = track;
            _nextRead = false;
        }
    }

    /// <summary>
    /// Disarms the next track. Mid-fade, the outgoing track comes back up to full
    /// gain over as long as it had been fading, instead of jumping back.
    /// </summary>
    public void ClearNext()
    {
        lock (_gate)
        {
            if (_crossfading && _nextRead)
            {
                var reached = (float)Math.Cos(
                    Math.Min(1.0, _fadeElapsedSamples / (double)_fadeTotalSamples) * Math.PI / 2);
                StartRamp(reached, Math.Max(MinRecoverFrames, _fadeElapsedSamples));
            }

            DropNext();
            _crossfading = false;
        }
    }

    private const long MinRecoverFrames = FfmpegDecoder.SampleRate / 100;

    private void DropNext()
    {
        _next?.Dispose();
        _next = null;
        _nextRead = false;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _current?.Dispose();
            _current = null;
            DropNext();
            _crossfading = false;
            StopRamp();
        }
    }

    public void BeginCrossfade(TimeSpan duration)
    {
        lock (_gate)
        {
            if (_next is null || _crossfading)
                return;
            _fadeTotalSamples = Math.Max(1, (long)(duration.TotalSeconds * FfmpegDecoder.SampleRate));
            _fadeElapsedSamples = 0;
            _crossfading = true;
        }
    }

    public void Mix(Span<float> output)
    {
        output.Clear();

        if (Paused)
            return;

        BufferedTrack? promoted = null;
        lock (_gate)
        {
            if (_current is null)
                return;

            if (_crossfading && _next is not null)
            {
                promoted = MixCrossfade(output);
            }
            else
            {
                var read = _current.Read(output);
                ApplyRamp(output[..read]);

                // Gapless handover: current is fully drained and a successor is armed.
                if (read < output.Length && _current.Ended && _current.Error is null && _next is not null)
                {
                    promoted = Promote();
                    _current!.Read(output[read..]);
                }
            }

            ApplyGain(output);
        }

        // Fired after releasing _gate: the handler takes the player's lock, and
        // control paths take player-lock → mixer-lock — firing under _gate would
        // invert that order and deadlock the device thread.
        if (promoted is not null)
            TrackStarted?.Invoke(promoted);
    }

    private void ApplyGain(Span<float> output)
    {
        var target = _targetGain;
        if (_gain == target)
        {
            if (target != 1f)
            {
                for (var i = 0; i < output.Length; i++)
                    output[i] *= target;
            }
            return;
        }

        // Exponential approach per frame (both channels share a gain), time
        // constant ~10 ms at 48 kHz: no zipper noise. In float the approach
        // stalls a few ulps short of the target, and toward zero it sinks into
        // subnormals, so once it is inaudibly close (-80 dB) it snaps to the target.
        const int channels = FfmpegDecoder.Channels;
        for (var frame = 0; frame < output.Length / channels; frame++)
        {
            if (_gain != target)
            {
                _gain += (target - _gain) * GainApproachPerFrame;
                if (Math.Abs(target - _gain) < GainSnapDistance)
                    _gain = target;
            }

            for (var ch = 0; ch < channels; ch++)
                output[frame * channels + ch] *= _gain;
        }
    }

    private const float GainApproachPerFrame = 0.002f;
    private const float GainSnapDistance = 1e-4f;

    private BufferedTrack? MixCrossfade(Span<float> output)
    {
        var outgoing = _current!;
        var incoming = _next!;
        const int channels = FfmpegDecoder.Channels;

        var frames = output.Length / channels;
        if (_nextScratch.Length < output.Length)
            _nextScratch = new float[output.Length];
        var incomingSpan = _nextScratch.AsSpan(0, output.Length);

        // Short reads (underrun / track end) leave silence in the unread tail.
        var outgoingRead = outgoing.Read(output); // output was cleared by Mix
        ApplyRamp(output[..outgoingRead]); // a fade-in it still owed carries on under the fade-out
        var incomingRead = incoming.Read(incomingSpan);
        _nextRead = true;
        incomingSpan[incomingRead..].Clear();

        for (var frame = 0; frame < frames; frame++)
        {
            var t = Math.Min(1.0, (_fadeElapsedSamples + frame) / (double)_fadeTotalSamples);
            var gainOut = (float)Math.Cos(t * Math.PI / 2);
            var gainIn = (float)Math.Sin(t * Math.PI / 2);

            for (var ch = 0; ch < channels; ch++)
            {
                var i = frame * channels + ch;
                output[i] = output[i] * gainOut + incomingSpan[i] * gainIn;
            }
        }

        _fadeElapsedSamples += frames;
        if (_fadeElapsedSamples >= _fadeTotalSamples)
        {
            _crossfading = false;
            return Promote();
        }

        if (outgoing.Ended)
        {
            // The outgoing track ran out before the fade did (its duration was
            // overstated, or the fade began late). Promote now, so the track
            // shown is the one heard, but finish the incoming track's fade-in
            // on time: jumping it to unity here would be an audible step.
            _crossfading = false;
            var reached = (float)Math.Sin(_fadeElapsedSamples / (double)_fadeTotalSamples * Math.PI / 2);
            var promoted = Promote();
            StartRamp(reached, _fadeTotalSamples - _fadeElapsedSamples);
            return promoted;
        }

        return null;
    }

    private void StartRamp(float from, long frames)
    {
        _rampFramesLeft = frames;
        _rampStep = (1.0 - from) / frames;
    }

    private void StopRamp() => _rampFramesLeft = 0;

    private void ApplyRamp(Span<float> samples)
    {
        const int channels = FfmpegDecoder.Channels;
        var frames = samples.Length / channels;
        for (var frame = 0; frame < frames && _rampFramesLeft > 0; frame++, _rampFramesLeft--)
        {
            // Computed from the frames left, not accumulated: no drift past unity.
            var gain = (float)(1.0 - _rampFramesLeft * _rampStep);
            for (var ch = 0; ch < channels; ch++)
                samples[frame * channels + ch] *= gain;
        }
    }

    /// <summary>
    /// Disposes the current track and promotes the preloaded one. Caller holds the
    /// lock and fires <see cref="TrackStarted"/> with the returned track after
    /// releasing it.
    /// </summary>
    private BufferedTrack Promote()
    {
        var promoted = _next!;
        _current!.Dispose();
        _current = promoted;
        _next = null;
        _nextRead = false;
        StopRamp();
        return promoted;
    }
}
