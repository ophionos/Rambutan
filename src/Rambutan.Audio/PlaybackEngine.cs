using Rambutan.Core.Diagnostics;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Enums;
using SoundFlow.Structs;
using SoundFlow.Structs.Events;

namespace Rambutan.Audio;

/// <summary>
/// Device output stage: opens a playback device via SoundFlow (miniaudio
/// backend) and pulls from the crossfade mixer through the equalizer, teeing every
/// buffer into the FFT analyzer. Without a chosen device it opens the system default as such, not
/// the device that is default now, so the OS moves the stream when the user
/// changes the default (PipeWire and PulseAudio move streams that named no
/// sink; miniaudio reroutes WASAPI's default). A device chosen by name is used
/// while it is present. If the device stops outside our control (unplugged,
/// backend error), recovery opens again by the same rule and moves the pull
/// graph over — the mixer and ring buffers never notice, so playback resumes
/// where it stalled.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private const int RecoveryAttempts = 3;
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromMilliseconds(500);

    private readonly AudioEngine _engine;
    private readonly object _deviceLock = new();
    private AudioPlaybackDevice _device;
    private readonly PullComponent _pull;
    private string? _preferred;
    private bool _started;
    private volatile bool _closing;
    private int _recovering;

    /// <summary>The output moved to another physical device and kept playing.</summary>
    public event Action<string>? DeviceRecovered;

    /// <summary>No output device could be (re)opened; playback is stalled.</summary>
    public event Action? DeviceLost;

    /// <param name="preferredDevice">A device's name to use while it is present;
    /// null follows the system default.</param>
    public PlaybackEngine(string? preferredDevice = null)
    {
        _engine = new MiniAudioEngine();
        _preferred = preferredDevice;
        _engine.UpdateAudioDevicesInfo();

        var devices = _engine.PlaybackDevices;
        if (devices.Length == 0)
            throw new InvalidOperationException("No playback devices available.");

        _pull = new PullComponent(_engine, AudioFormat.DvdHq, Mixer, Equalizer, Analyzer);
        _device = Open(devices);
        _device.MasterMixer.AddComponent(_pull);
        _engine.DeviceStopped += OnDeviceStopped;
        AppLog.Write("audio", $"output device: {Describe()} ({devices.Length} available)");
    }

    /// <summary>The chosen device when it is present, otherwise the system default
    /// (a null device for SoundFlow). Sets <see cref="DeviceName"/> and pacing for it.</summary>
    private AudioPlaybackDevice Open(DeviceInfo[] devices)
    {
        DeviceInfo? chosen = OutputDeviceChoice.Find(devices.Select(d => d.Name), _preferred) is { } i
            ? devices[i]
            : null;
        var device = _engine.InitializePlaybackDevice(chosen, AudioFormat.DvdHq);
        FollowsDefault = chosen is null;
        DeviceName = (chosen ?? devices.FirstOrDefault(d => d.IsDefault, devices[0])).Name ?? "(unknown)";
        _pull.Paced = IsNullDevice;
        return device;
    }

    /// <summary>Moves the pull graph onto <paramref name="next"/> and closes the
    /// old device. Under <see cref="_deviceLock"/>.</summary>
    private void MoveTo(AudioPlaybackDevice next)
    {
        var old = _device;
        // Set first: stopping the old device raises DeviceStopped, which is ignored
        // for any device but the current one.
        _device = next;
        old.MasterMixer.RemoveComponent(_pull);
        next.MasterMixer.AddComponent(_pull);
        try
        {
            old.Stop();
            old.Dispose();
        }
        catch (Exception ex)
        {
            // A device that died under us may refuse to stop; it is let go either way.
            AppLog.Write("audio", $"closing the old output: {ex.Message}");
        }
        if (_started)
            next.Start();
    }

    private string Describe() => FollowsDefault ? $"system default, now {DeviceName}" : DeviceName;

    /// <summary>True when the output follows the system default rather than a
    /// device chosen by name.</summary>
    public bool FollowsDefault { get; private set; }

    public IReadOnlyList<string> ListDevices()
    {
        lock (_deviceLock)
        {
            _engine.UpdateAudioDevicesInfo();
            return [.. _engine.PlaybackDevices.Select(d => d.Name).OfType<string>().Where(n => n.Length > 0).Distinct()];
        }
    }

    /// <summary>Uses the device named <paramref name="name"/> while it is present,
    /// or follows the system default when null; playback moves over at once.</summary>
    public void SetPreferredDevice(string? name)
    {
        lock (_deviceLock)
        {
            if (_closing || _preferred == name)
                return;
            _preferred = name;
            _engine.UpdateAudioDevicesInfo();
            var devices = _engine.PlaybackDevices;
            if (devices.Length == 0)
                return;
            MoveTo(Open(devices));
            AppLog.Write("audio", $"output device: {Describe()}");
        }
    }

    public CrossfadeMixer Mixer { get; } = new();
    public Equalizer Equalizer { get; } = new();
    public FftAnalyzer Analyzer { get; } = new();
    public string DeviceName { get; private set; } = "(unknown)";

    /// <summary>A silent output: audio "plays" into the void. Callers surface this,
    /// never silent playback unannounced. Two kinds are known: miniaudio's null
    /// backend (seen on WSL), and ALSA's null plugin, which a Linux machine with no
    /// sound card offers as its only device. ALSA's describes itself as "Discard all
    /// samples …" without the word null, and it isn't paced, so playback runs
    /// faster than real time through it.</summary>
    public bool IsNullDevice =>
        DeviceName.Contains("null", StringComparison.OrdinalIgnoreCase) ||
        DeviceName.StartsWith("Discard all samples", StringComparison.OrdinalIgnoreCase);

    public void Start()
    {
        lock (_deviceLock)
        {
            _device.Start();
            _started = true;
        }
    }

    /// <summary>Fires on the backend's notification path — hand recovery to a
    /// worker; SwitchDevice from inside the callback would re-enter the backend.</summary>
    private void OnDeviceStopped(object? sender, DeviceEventArgs e)
    {
        if (_closing || !ReferenceEquals(e.Device, _device))
            return;
        if (Interlocked.Exchange(ref _recovering, 1) == 1)
            return;
        _ = Task.Run(RecoverDevice);
    }

    private void RecoverDevice()
    {
        try
        {
            for (var attempt = 0; attempt < RecoveryAttempts && !_closing; attempt++)
            {
                Thread.Sleep(RecoveryDelay); // give the OS a beat to settle the device list
                try
                {
                    lock (_deviceLock)
                    {
                        if (_closing)
                            return;
                        _engine.UpdateAudioDevicesInfo();
                        var devices = _engine.PlaybackDevices;
                        if (devices.Length == 0)
                            continue;
                        _started = true;
                        MoveTo(Open(devices));
                    }

                    AppLog.Write("audio", $"output recovered: {Describe()}");
                    DeviceRecovered?.Invoke(DeviceName);
                    return;
                }
                catch
                {
                    // Device list can churn while the OS settles; try again.
                }
            }

            if (!_closing)
                DeviceLost?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _recovering, 0);
        }
    }

    public void Dispose()
    {
        _closing = true;
        _engine.DeviceStopped -= OnDeviceStopped;
        lock (_deviceLock)
        {
            _device.MasterMixer.RemoveComponent(_pull);
            _device.Stop();
            _device.Dispose();
        }
        _engine.Dispose();
    }

    /// <summary>Bridges SoundFlow's pull graph to the Rambutan mixer.</summary>
    private sealed class PullComponent(
        AudioEngine engine, AudioFormat format, CrossfadeMixer mixer, Equalizer equalizer, FftAnalyzer analyzer)
        : SoundComponent(engine, format)
    {
        private readonly int _sampleRate = format.SampleRate;
        private readonly System.Diagnostics.Stopwatch _clock = new();
        private long _framesSincePaceStart;
        private volatile bool _paced;

        public override string Name { get; set; } = "RambutanMixer";

        /// <summary>
        /// Holds the pull to real time. A sound card paces its callbacks by
        /// consuming samples at the sample rate; ALSA's null plugin (the only
        /// device on a Linux machine with no sound card) consumes them instantly,
        /// so without this, tracks finish in seconds and the queue runs out while
        /// the UI claims to be playing, pinning a core the whole time. Set for
        /// silent devices only; a real device never waits here.
        /// </summary>
        public bool Paced
        {
            get => _paced;
            set
            {
                _framesSincePaceStart = 0;
                _clock.Restart();
                _paced = value;
            }
        }

        protected override void GenerateAudio(Span<float> buffer, int channels)
        {
            mixer.Mix(buffer);
            equalizer.Process(buffer);
            analyzer.Feed(buffer);

            if (!_paced || channels <= 0)
                return;

            _framesSincePaceStart += buffer.Length / channels;
            var ahead = TimeSpan.FromSeconds((double)_framesSincePaceStart / _sampleRate)
                        - _clock.Elapsed;
            if (ahead > TimeSpan.FromMilliseconds(5))
            {
                Thread.Sleep(ahead);
            }
            else if (ahead < TimeSpan.FromMilliseconds(-500))
            {
                // Fell well behind (the process was suspended, say): start over
                // rather than rush to catch up.
                _framesSincePaceStart = 0;
                _clock.Restart();
            }
        }
    }
}
