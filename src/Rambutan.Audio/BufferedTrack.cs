using Rambutan.Core.Playback;

namespace Rambutan.Audio;

/// <summary>
/// One playable decode chain: a decode thread pumping an <see cref="FfmpegDecoder"/>
/// into a ring buffer. Constructing one and letting it fill IS the gapless preload
/// — the next track is network-fetched, decoded, and buffered before the
/// device callback ever touches it.
/// </summary>
public sealed class BufferedTrack : IDisposable
{
    private const int BufferSeconds = 10;
    private const int DecodeChunkSamples = 8192;

    private readonly FloatRingBuffer _ring =
        new(FfmpegDecoder.SampleRate * FfmpegDecoder.Channels * BufferSeconds);
    private readonly Thread? _thread;
    private readonly TimeSpan _startOffset;
    private volatile Exception? _decodeError;
    private volatile bool _decoderReady;
    private FfmpegDecoder? _decoder;

    private readonly float _gain;

    public BufferedTrack(string url, string displayName, TimeSpan? seekTo = null, float gain = 1f)
    {
        DisplayName = displayName;
        _gain = gain;
        _startOffset = seekTo ?? TimeSpan.Zero;
        _thread = new Thread(() => DecodeLoop(url, seekTo))
        {
            IsBackground = true,
            Name = $"audio-decode {displayName}",
        };
        _thread.Start();
    }

    /// <summary>
    /// A track over samples already in hand, with no decoder or thread: the
    /// samples go straight into the ring, so a test drives the mixer
    /// deterministically. <paramref name="complete"/> false leaves the track
    /// open, as a stream that has not ended yet.
    /// </summary>
    internal BufferedTrack(string displayName, ReadOnlySpan<float> samples, bool complete = true, float gain = 1f)
    {
        if (samples.Length > _ring.Capacity)
            throw new ArgumentOutOfRangeException(nameof(samples), "more samples than the ring holds");
        DisplayName = displayName;
        _gain = gain;
        _ring.Write(samples);
        if (complete)
            _ring.Complete();
    }

    public string DisplayName { get; }

    /// <summary>Non-null when the decode thread died; the track reads as ended.</summary>
    public Exception? Error => _decodeError;

    /// <summary>Playback position — samples handed to the device, plus the seek offset.</summary>
    public TimeSpan Position => _startOffset + TimeSpan.FromSeconds(
        _ring.TotalRead / (double)(FfmpegDecoder.SampleRate * FfmpegDecoder.Channels));

    public TimeSpan? Duration => _decoderReady ? _decoder?.Duration : null;
    public string? CodecName => _decoderReady ? _decoder?.CodecName : null;
    public int BufferedSamples => _ring.Count;
    public bool Ended => _ring.IsCompleted;

    /// <summary>
    /// Non-blocking read for the device callback. Returns samples written; never
    /// throws (a decode fault must not take down the audio thread — check
    /// <see cref="Error"/> from a control thread instead). Applies the track's
    /// normalization gain, so every mixer path — solo, gapless handover,
    /// crossfade — reads level-matched samples.
    /// </summary>
    public int Read(Span<float> destination)
    {
        var read = _ring.Read(destination);
        if (_gain != 1f)
        {
            for (var i = 0; i < read; i++)
                destination[i] *= _gain;
        }

        return read;
    }

    /// <summary>Waits until at least <paramref name="ahead"/> of audio is buffered (or the track ends).</summary>
    public async Task PrebufferAsync(TimeSpan ahead, CancellationToken ct = default)
    {
        var target = Math.Min(
            (int)(ahead.TotalSeconds * FfmpegDecoder.SampleRate * FfmpegDecoder.Channels),
            _ring.Capacity);

        while (_ring.Count < target && !_ring.IsCompleted)
        {
            ThrowIfFaulted();
            await Task.Delay(25, ct);
        }

        // A decoder that dies instantly completes the ring before the loop runs
        // once, so the fault must be re-checked on the way out — otherwise this
        // reports success and the caller plays a silent, already-ended track.
        ThrowIfFaulted();
    }

    private void ThrowIfFaulted()
    {
        if (_decodeError is { } error)
        {
            // Preserve the open-vs-decode distinction across the thread boundary —
            // the coordinator retries open failures with a freshly resolved URL.
            throw error is StreamOpenException
                ? new StreamOpenException($"prebuffer of '{DisplayName}' failed: {error.Message}", error)
                : new FfmpegException($"prebuffer of '{DisplayName}' failed: {error.Message}");
        }
    }

    private void DecodeLoop(string url, TimeSpan? seekTo)
    {
        try
        {
            FfmpegDecoder decoder;
            try
            {
                decoder = new FfmpegDecoder(url, seekTo);
            }
            catch (Exception ex)
            {
                throw new StreamOpenException(ex.Message, ex);
            }

            using var _ = decoder;
            _decoder = decoder;
            _decoderReady = true;

            Span<float> chunk = stackalloc float[DecodeChunkSamples];
            while (true)
            {
                var read = decoder.Read(chunk);
                if (read == 0)
                    break;
                _ring.Write(chunk[..read]);

                // A closed ring drops writes instead of blocking, so without this
                // check an abandoned track would keep downloading to the very end.
                if (_ring.IsClosed)
                    break;
            }
        }
        catch (Exception ex)
        {
            _decodeError = ex;
        }
        finally
        {
            _ring.Complete();
        }
    }

    public void Dispose()
    {
        // Unblocks the writer; the background decode thread exits on its own.
        _ring.Complete();
    }
}
