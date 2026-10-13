using Rambutan.Core.Playback;

namespace Rambutan.Audio;

/// <summary>
/// IAudioPlayer adapter over the engine (PlaybackEngine + CrossfadeMixer +
/// BufferedTrack). Owns the progress timer and translates engine happenings
/// (promotion, drain, decode fault) into coordinator events. Requires
/// <see cref="FfmpegRuntime.Initialize"/> to have run. After
/// <see cref="OutputIdleTimer.Delay"/> paused or stopped, it stops the output
/// device and the progress timer; play and resume start them again.
/// </summary>
public sealed class EngineAudioPlayer : IAudioPlayer, IDisposable
{
    private static readonly TimeSpan PlayPrebuffer = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NextPrebuffer = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SeekPrebuffer = TimeSpan.FromSeconds(2);

    private readonly object _sync = new();
    private PlaybackEngine? _engine;
    private Timer? _timer;
    private bool _deviceStarted;

    private Slot? _current;
    private Slot? _next;
    private string? _endedRaisedFor;
    private string? _faultRaisedFor;
    private double _volume = 1.0;
    private string? _outputDevice;
    private bool _equalizerOn;
    private IReadOnlyList<double> _equalizerGains = [];
    private long _chains;
    private readonly OutputIdleTimer _idle;

    public EngineAudioPlayer() => _idle = new OutputIdleTimer(StopIdleOutput);

    /// <param name="Chain">Names this decode chain in progress ticks; a seek keeps the
    /// key and mints a new one.</param>
    private sealed record Slot(string Key, BufferedTrack Track, AudioTrackRequest Request, long Chain);

    public event Action<AudioPlayerEvent>? EventRaised;

    public FftAnalyzer? Analyzer => _engine?.Analyzer;
    public string? DeviceName => _engine?.DeviceName;

    /// <summary>The output device chosen by name, used while it is present; null
    /// follows the system default. Set before the engine exists, it applies when
    /// the engine opens; after, playback moves over at once (a device switch, so
    /// not on the UI thread).</summary>
    public string? OutputDevice
    {
        get => _outputDevice;
        set
        {
            PlaybackEngine? engine;
            lock (_sync)
            {
                _outputDevice = value;
                engine = _engine;
            }
            engine?.SetPreferredDevice(value);
        }
    }

    /// <summary>The equalizer's switch and its gains, one per band of
    /// <see cref="Equalizer.Frequencies"/>, in dB. Set before the engine exists,
    /// they apply when it opens.</summary>
    public void SetEqualizer(bool on, IReadOnlyList<double> gainsDb)
    {
        lock (_sync)
        {
            _equalizerOn = on;
            _equalizerGains = [.. gainsDb];
            _engine?.Equalizer.Set(on, _equalizerGains);
        }
    }

    public IReadOnlyList<string> OutputDevices()
    {
        try
        {
            return EnsureEngine().ListDevices();
        }
        catch (Exception ex)
        {
            Rambutan.Core.Diagnostics.AppLog.Write("audio", $"listing output devices: {ex.Message}");
            return [];
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            lock (_sync)
            {
                if (_engine is not null)
                    _engine.Mixer.MasterGain = _volume;
            }
        }
    }

    public async Task PlayAsync(AudioTrackRequest request, CancellationToken ct = default)
    {
        _idle.Disarm();
        var engine = EnsureEngine();
        var track = await PrepareTrackAsync(request, request.StartAt, PlayPrebuffer, ct);

        lock (_sync)
        {
            _current = new Slot(request.Key, track, request, ++_chains);
            _endedRaisedFor = null;
            _faultRaisedFor = null;
            engine.Mixer.Paused = false;
            engine.Mixer.Play(track); // keeps any armed next; coordinator clears explicitly
            ForgetNextIfDropped(engine);
        }

        StartDevice(engine);
    }

    public async Task PreloadNextAsync(AudioTrackRequest request, CancellationToken ct = default)
    {
        var engine = EnsureEngine();
        var track = await PrepareTrackAsync(request, TimeSpan.Zero, NextPrebuffer, ct);

        lock (_sync)
        {
            _next = new Slot(request.Key, track, request, ++_chains);
            engine.Mixer.SetNext(track);
        }
    }

    public void ClearNext()
    {
        lock (_sync)
        {
            _next = null;
            _engine?.Mixer.ClearNext();
        }
    }

    public void BeginCrossfade(TimeSpan duration) => _engine?.Mixer.BeginCrossfade(duration);

    public async Task<SeekResult> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        Slot? current;
        lock (_sync) current = _current;
        if (current is null || _engine is null)
            return new SeekResult(NextKept: true, Chain: null);

        var request = current.Request with { StartAt = position };
        var track = await PrepareTrackAsync(request, position, SeekPrebuffer, ct);

        lock (_sync)
        {
            // A crossfade or a gapless handover moved on while the new chain
            // prebuffered: the track this seek was for is gone, so the seek is too.
            if (!ReferenceEquals(_current, current) || !_engine.Mixer.Replace(current.Track, track))
            {
                track.Dispose();
                return new SeekResult(NextKept: true, Chain: null);
            }

            // Same key: the logical track hasn't changed, only its decode chain.
            _current = new Slot(current.Key, track, request, ++_chains);
            _endedRaisedFor = null;
            _faultRaisedFor = null;
            return new SeekResult(NextKept: !ForgetNextIfDropped(_engine), Chain: _current.Chain);
        }
    }

    /// <summary>
    /// A cut during a crossfade makes the mixer drop the part-read next track.
    /// With no successor, the current track's end surfaces as Ended and the
    /// coordinator plays the next track from its start, unless it arms it again
    /// (a seek reports the drop). Caller holds the lock. Returns true on a drop.
    /// </summary>
    private bool ForgetNextIfDropped(PlaybackEngine engine)
    {
        if (_next is null || engine.Mixer.HasNext)
            return false;
        _next = null;
        return true;
    }

    public void Pause()
    {
        if (_engine is null)
            return;
        _engine.Mixer.Paused = true;
        _idle.Arm();
    }

    public void Resume()
    {
        if (_engine is not { } engine)
            return;
        _idle.Disarm();
        try
        {
            StartDevice(engine);
        }
        catch (Exception ex)
        {
            // The output stopped for idleness and no device opens now: the same
            // honest state as a device lost while playing.
            Rambutan.Core.Diagnostics.AppLog.Write("audio", $"resuming the output: {ex.Message}");
            Task.Run(() => EventRaised?.Invoke(new DeviceLostEvent()));
            return;
        }
        engine.Mixer.Paused = false;
    }

    public void Stop()
    {
        lock (_sync)
        {
            _current = null;
            _next = null;
            if (_engine is not null)
            {
                _engine.Mixer.Clear();
                _engine.Mixer.Paused = false;
            }
        }
        if (_engine is not null)
            _idle.Arm();
    }

    /// <summary>On the idle timer's thread. A device that is already stopped (a
    /// stop before the first play) is left alone.</summary>
    private void StopIdleOutput()
    {
        lock (_sync)
        {
            if (_engine is null || !_deviceStarted)
                return;
            _engine.Stop();
            _deviceStarted = false;
            _timer?.Dispose();
            _timer = null;
        }
        Rambutan.Core.Diagnostics.AppLog.Write(
            "audio", $"output stopped after {_idle.Delay} with nothing playing");
    }

    private static async Task<BufferedTrack> PrepareTrackAsync(
        AudioTrackRequest request, TimeSpan startAt, TimeSpan prebuffer, CancellationToken ct)
    {
        var track = new BufferedTrack(
            request.Url, request.DisplayName, startAt > TimeSpan.Zero ? startAt : null,
            NormalizationGain(request.LoudnessDb));
        try
        {
            await track.PrebufferAsync(prebuffer, ct);
            return track;
        }
        catch
        {
            track.Dispose();
            throw;
        }
    }

    /// <summary>
    /// loudnessDb → linear gain, attenuate-only: tracks louder than the
    /// platform reference come down to it; quieter tracks stay at unity rather
    /// than being boosted into clipping (no limiter in the chain).
    /// </summary>
    private static float NormalizationGain(double? loudnessDb) =>
        loudnessDb is { } db and > 0 ? (float)Math.Pow(10, -db / 20.0) : 1f;

    private PlaybackEngine EnsureEngine()
    {
        lock (_sync)
        {
            if (_engine is null)
            {
                _engine = new PlaybackEngine(_outputDevice);
                _engine.Mixer.MasterGain = _volume;
                _engine.Equalizer.Set(_equalizerOn, _equalizerGains);
                _engine.Mixer.TrackStarted += OnTrackStarted;
                // Off the engine's threads before touching subscribers.
                _engine.DeviceRecovered += name =>
                    Task.Run(() => EventRaised?.Invoke(new DeviceChangedEvent(name)));
                _engine.DeviceLost += () =>
                    Task.Run(() => EventRaised?.Invoke(new DeviceLostEvent()));
            }
            return _engine;
        }
    }

    private void StartDevice(PlaybackEngine engine)
    {
        bool announceNullDevice = false;
        lock (_sync)
        {
            if (!_deviceStarted)
            {
                engine.Start();
                _deviceStarted = true;
                // WSL finding: miniaudio can open a silent null backend and "play"
                // into the void — announce it, never silent playback unannounced.
                announceNullDevice = engine.IsNullDevice;
            }
            // After the start, so a device that fails to start leaves no timer ticking.
            _timer ??= new Timer(OnTick, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        }

        if (announceNullDevice)
            Task.Run(() => EventRaised?.Invoke(new DeviceChangedEvent(engine.DeviceName)));
    }

    /// <summary>Runs on the audio thread (mixer lock already released) — hand off immediately.</summary>
    private void OnTrackStarted(BufferedTrack started)
    {
        Slot? promoted = null;
        lock (_sync)
        {
            if (_next is { } next && ReferenceEquals(next.Track, started))
            {
                _current = next;
                _next = null;
                _endedRaisedFor = null;
                _faultRaisedFor = null;
                promoted = next;
            }
        }

        if (promoted is not null)
            Task.Run(() => EventRaised?.Invoke(new TrackPromotedEvent(promoted.Key)));
    }

    private void OnTick(object? state)
    {
        // Decide-and-mark happens under the lock so overlapping ticks (a stalled
        // callback plus the next period) can't both claim the same ended/fault;
        // the event itself is raised after release.
        AudioPlayerEvent? toRaise;
        lock (_sync)
        {
            if (_current is not { } current)
                return;

            var track = current.Track;

            // The fault is raised once, as soon as the decoder fails; progress
            // carries on while the mixer plays out what was buffered, so a
            // coordinator waiting for the network sees the position move and then
            // the starvation. A faulted track never reports Ended.
            if (track.Error is { } error && _faultRaisedFor != current.Key)
            {
                _faultRaisedFor = current.Key;
                toRaise = new PlayerFaultedEvent(current.Key, error.Message);
            }
            else if (track.Ended && track.Error is null)
            {
                // With a successor armed the mixer promotes inside the callback;
                // only a drain with nothing armed surfaces as Ended.
                if (_next is not null || _endedRaisedFor == current.Key)
                    return;
                _endedRaisedFor = current.Key;
                toRaise = new PlaybackEndedEvent(current.Key);
            }
            else
            {
                var paused = _engine?.Mixer.Paused ?? false;
                var buffered = TimeSpan.FromSeconds(
                    track.BufferedSamples / (double)(FfmpegDecoder.SampleRate * FfmpegDecoder.Channels));
                var starved = !paused && track.BufferedSamples == 0;
                toRaise = new ProgressEvent(
                    current.Key, track.Position, buffered, track.Duration, starved, current.Chain);
            }
        }

        EventRaised?.Invoke(toRaise);
    }

    public void Dispose()
    {
        _idle.Dispose();
        _timer?.Dispose();
        lock (_sync)
        {
            if (_engine is not null)
            {
                _engine.Mixer.TrackStarted -= OnTrackStarted;
                _engine.Mixer.Clear();
                _engine.Dispose();
                _engine = null;
            }
        }
    }
}
