using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace Rambutan.Audio;

/// <summary>
/// Decodes one audio source (googlevideo URL or local file) to interleaved
/// 32-bit-float PCM at the engine's canonical format (48 kHz stereo).
/// Network I/O happens inside <see cref="Read"/> — callers run it on a decode
/// thread, never on the device callback.
/// </summary>
public sealed unsafe class FfmpegDecoder : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private SwrContext* _swr;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private readonly int _streamIndex;

    private float[] _pending = [];
    private int _pendingOffset;
    private int _pendingCount;
    private bool _draining;
    private bool _ended;

    // Seek landing correction: av_seek_frame(BACKWARD) lands on the cue/keyframe
    // BEFORE the target (measured ~10 s early on YouTube WebM/Opus, whose cues are
    // ~10 s apart), so the gap is decoded and discarded to make the first served
    // sample the target. Position accounting upstream assumes exactly this.
    private double _seekTargetSeconds = -1; // <0: no discard pending
    private long _samplesToDiscard;

    public FfmpegDecoder(string url, TimeSpan? seekTo = null)
    {
        AVDictionary* options = null;
        // Googlevideo edges drop idle connections; ffmpeg's own reconnect logic
        // resumes with Range requests.
        ffmpeg.av_dict_set(&options, "reconnect", "1", 0);
        ffmpeg.av_dict_set(&options, "reconnect_streamed", "1", 0);
        ffmpeg.av_dict_set(&options, "reconnect_delay_max", "5", 0);
        ffmpeg.av_dict_set(&options, "rw_timeout", "15000000", 0); // 15 s, microseconds

        AVFormatContext* format = null;
        try
        {
            // On open failure ffmpeg frees the context itself, so _format stays null.
            Check(ffmpeg.avformat_open_input(&format, url, null, &options), "open input");
            _format = format;
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }

        // From here on, native contexts are live — release them if any later
        // init step throws, since the caller never gets an object to Dispose.
        try
        {
            Check(ffmpeg.avformat_find_stream_info(_format, null), "find stream info");

            AVCodec* codec = null;
            _streamIndex = ffmpeg.av_find_best_stream(
                _format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
            Check(_streamIndex, "find audio stream");

            _codec = ffmpeg.avcodec_alloc_context3(codec);
            Check(ffmpeg.avcodec_parameters_to_context(
                _codec, _format->streams[_streamIndex]->codecpar), "codec parameters");
            Check(ffmpeg.avcodec_open2(_codec, codec, null), "open codec");

            CodecName = ffmpeg.avcodec_get_name(_codec->codec_id);

            AVChannelLayout outLayout;
            ffmpeg.av_channel_layout_default(&outLayout, Channels);
            SwrContext* swr = null;
            Check(ffmpeg.swr_alloc_set_opts2(
                &swr,
                &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, SampleRate,
                &_codec->ch_layout, _codec->sample_fmt, _codec->sample_rate,
                0, null), "swr options");
            _swr = swr;
            Check(ffmpeg.swr_init(_swr), "swr init");

            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();

            if (_format->duration > 0)
                Duration = TimeSpan.FromSeconds(_format->duration / (double)ffmpeg.AV_TIME_BASE);

            if (seekTo is { } target && target > TimeSpan.Zero)
            {
                var ts = (long)(target.TotalSeconds * ffmpeg.AV_TIME_BASE);
                Check(ffmpeg.av_seek_frame(_format, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD), "seek");
                _seekTargetSeconds = target.TotalSeconds;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string CodecName { get; }
    public TimeSpan? Duration { get; }

    /// <summary>
    /// Fills <paramref name="destination"/> with interleaved float samples.
    /// Returns the number of samples written; 0 means end of stream.
    /// </summary>
    public int Read(Span<float> destination)
    {
        var written = 0;
        while (written < destination.Length)
        {
            if (_pendingCount > 0 && _samplesToDiscard > 0)
            {
                var drop = (int)Math.Min(_samplesToDiscard, _pendingCount);
                _pendingOffset += drop;
                _pendingCount -= drop;
                _samplesToDiscard -= drop;
                continue;
            }

            if (_pendingCount > 0)
            {
                var take = Math.Min(_pendingCount, destination.Length - written);
                _pending.AsSpan(_pendingOffset, take).CopyTo(destination[written..]);
                _pendingOffset += take;
                _pendingCount -= take;
                written += take;
                continue;
            }

            if (_ended || !DecodeNextFrame())
                break;
        }

        return written;
    }

    /// <summary>Decodes the next frame into the pending buffer. False at end of stream.</summary>
    private bool DecodeNextFrame()
    {
        while (true)
        {
            var receive = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (receive == 0)
            {
                if (_seekTargetSeconds >= 0)
                    ArmSeekDiscard(_frame);
                ConvertFrame(_frame);
                ffmpeg.av_frame_unref(_frame);
                return true;
            }

            if (receive == ffmpeg.AVERROR_EOF)
            {
                FlushResampler();
                _ended = true;
                return _pendingCount > 0;
            }

            if (receive != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                Check(receive, "receive frame");

            // Decoder wants more input.
            if (_draining)
                continue;

            var read = ffmpeg.av_read_frame(_format, _packet);
            if (read == ffmpeg.AVERROR_EOF)
            {
                Check(ffmpeg.avcodec_send_packet(_codec, null), "send flush packet");
                _draining = true;
                continue;
            }

            Check(read, "read frame");

            if (_packet->stream_index == _streamIndex)
                Check(ffmpeg.avcodec_send_packet(_codec, _packet), "send packet");

            ffmpeg.av_packet_unref(_packet);
        }
    }

    /// <summary>Converts the first post-seek frame's landing timestamp into a count
    /// of output samples to swallow, making the seek sample-accurate.</summary>
    private void ArmSeekDiscard(AVFrame* frame)
    {
        var target = _seekTargetSeconds;
        _seekTargetSeconds = -1;

        var ts = frame->best_effort_timestamp;
        if (ts == ffmpeg.AV_NOPTS_VALUE)
            return; // no landing info — keep the old behavior rather than guess

        var tb = _format->streams[_streamIndex]->time_base;
        var landing = ts * tb.num / (double)tb.den;
        var gap = target - landing;
        if (gap > 0)
            _samplesToDiscard = (long)Math.Round(gap * SampleRate) * Channels;
    }

    private void ConvertFrame(AVFrame* frame)
    {
        var maxOut = (int)ffmpeg.swr_get_out_samples(_swr, frame->nb_samples);
        EnsurePendingCapacity(maxOut * Channels);

        fixed (float* pendingPtr = _pending)
        {
            var outPtr = (byte*)pendingPtr;
            var converted = ffmpeg.swr_convert(
                _swr, &outPtr, maxOut, frame->extended_data, frame->nb_samples);
            Check(converted, "swr convert");
            _pendingOffset = 0;
            _pendingCount = converted * Channels;
        }
    }

    private void FlushResampler()
    {
        var maxOut = (int)ffmpeg.swr_get_out_samples(_swr, 0);
        if (maxOut <= 0)
            return;

        EnsurePendingCapacity(maxOut * Channels);
        fixed (float* pendingPtr = _pending)
        {
            var outPtr = (byte*)pendingPtr;
            var converted = ffmpeg.swr_convert(_swr, &outPtr, maxOut, null, 0);
            if (converted > 0)
            {
                _pendingOffset = 0;
                _pendingCount = converted * Channels;
            }
        }
    }

    private void EnsurePendingCapacity(int samples)
    {
        if (_pending.Length < samples)
            _pending = new float[samples];
    }

    private static void Check(int result, string operation)
    {
        if (result >= 0)
            return;

        const int bufferSize = 512;
        var buffer = stackalloc byte[bufferSize];
        ffmpeg.av_strerror(result, buffer, bufferSize);
        var message = Marshal.PtrToStringUTF8((nint)buffer) ?? "unknown error";
        throw new FfmpegException($"{operation} failed: {message} ({result})");
    }

    public void Dispose()
    {
        if (_frame is not null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_swr is not null)
        {
            var swr = _swr;
            ffmpeg.swr_free(&swr);
            _swr = null;
        }

        if (_codec is not null)
        {
            var codec = _codec;
            ffmpeg.avcodec_free_context(&codec);
            _codec = null;
        }

        if (_format is not null)
        {
            var format = _format;
            ffmpeg.avformat_close_input(&format);
            _format = null;
        }
    }
}

public sealed class FfmpegException(string message) : Exception(message);
