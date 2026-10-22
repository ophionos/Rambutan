using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Rambutan.Core.Local;

namespace Rambutan.Audio;

/// <summary>
/// Reads a local file's tags, length, ReplayGain and cover through libav,
/// without decoding any audio. Needs <see cref="FfmpegRuntime.Initialize"/> to
/// have run. Thread-safe: each call has its own format context.
/// <para>
/// libav's own format detection reads a large ID3v2 tag several times over
/// (measured up to 25 ms per MP3), and going by the extension instead gives wrong
/// data without an error for a misnamed file. So the tag is skipped here and the
/// format detected from what follows it. Stream info, which reads into the
/// audio, is looked for only when the open gives no length.
/// </para>
/// </summary>
public sealed unsafe class FfmpegMediaFileReader : IMediaFileReader
{
    private const int SniffBytes = 16 * 1024;

    public LocalFileTags? Read(string path)
    {
        AVFormatContext* format = null;
        try
        {
            var detected = Detect(path);
            if (ffmpeg.avformat_open_input(&format, path, detected, null) < 0)
            {
                // A sniff that guessed wrong: let libav look for itself.
                if (detected is null || ffmpeg.avformat_open_input(&format, path, null, null) < 0)
                    return null;
            }

            var stream = FirstAudioStream(format);
            if (Duration(format, stream) is null || stream is null)
            {
                if (ffmpeg.avformat_find_stream_info(format, null) < 0)
                    return null;
                stream = FirstAudioStream(format);
            }
            if (stream is null)
                return null;

            AVDictionary* tags = format->metadata;
            // Ogg and Opus keep their comments on the stream, not the container.
            AVDictionary* streamTags = stream->metadata;
            string? Tag(string key) => Get(tags, key) ?? Get(streamTags, key);

            return new LocalFileTags(
                Codec: ffmpeg.avcodec_get_name(stream->codecpar->codec_id) ?? "",
                Duration: Duration(format, stream),
                Title: LocalTagValues.Text(Tag("title")),
                Artist: LocalTagValues.Text(Tag("artist")),
                Album: LocalTagValues.Text(Tag("album")),
                AlbumArtist: LocalTagValues.Text(Tag("album_artist")),
                Genre: LocalTagValues.Text(Tag("genre")),
                TrackNumber: LocalTagValues.Position(Tag("track")),
                DiscNumber: LocalTagValues.Position(Tag("disc")),
                Year: LocalTagValues.Year(Tag("date")) ?? LocalTagValues.Year(Tag("year")),
                TrackGainDb: LocalTagValues.GainDb(Tag("REPLAYGAIN_TRACK_GAIN"))
                    ?? LocalTagValues.R128AsReplayGainDb(Tag("R128_TRACK_GAIN"))
                    ?? SideDataGain(stream, album: false),
                AlbumGainDb: LocalTagValues.GainDb(Tag("REPLAYGAIN_ALBUM_GAIN"))
                    ?? LocalTagValues.R128AsReplayGainDb(Tag("R128_ALBUM_GAIN"))
                    ?? SideDataGain(stream, album: true),
                Cover: Cover(format));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (format is not null)
                ffmpeg.avformat_close_input(&format);
        }
    }

    /// <summary>The format from the bytes after any ID3v2 tag; null when no format
    /// is sure enough, and libav then detects it the slow way.</summary>
    private static AVInputFormat* Detect(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1);
        file.Position = Id3v2Length(file);

        var buffer = (byte*)ffmpeg.av_mallocz(SniffBytes + ffmpeg.AVPROBE_PADDING_SIZE);
        var name = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            var read = file.ReadAtLeast(new Span<byte>(buffer, SniffBytes), SniffBytes, throwOnEndOfStream: false);
            if (read == 0)
                return null;
            var probe = new AVProbeData { filename = (byte*)name, buf = buffer, buf_size = read };
            var score = ffmpeg.AVPROBE_SCORE_RETRY;
            return ffmpeg.av_probe_input_format2(&probe, 1, &score);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
            ffmpeg.av_free(buffer);
        }
    }

    /// <summary>The length of a leading ID3v2 tag, header and footer included; 0
    /// when the file doesn't start with one.</summary>
    internal static long Id3v2Length(Stream file)
    {
        Span<byte> header = stackalloc byte[10];
        if (file.ReadAtLeast(header, 10, throwOnEndOfStream: false) < 10
            || header[0] != 'I' || header[1] != 'D' || header[2] != '3')
            return 0;
        // The size is four 7-bit bytes ("synchsafe"); flag 0x10 adds a 10-byte footer.
        var size = (header[6] & 0x7f) << 21 | (header[7] & 0x7f) << 14 | (header[8] & 0x7f) << 7 | (header[9] & 0x7f);
        return 10 + size + ((header[5] & 0x10) != 0 ? 10 : 0);
    }

    /// <summary>The first audio stream. Not <c>av_find_best_stream</c>: it fails
    /// for MP3 and FLAC before stream info has been read.</summary>
    private static AVStream* FirstAudioStream(AVFormatContext* format)
    {
        for (var i = 0; i < format->nb_streams; i++)
        {
            var stream = format->streams[i];
            if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                return stream;
        }
        return null;
    }

    /// <summary>The stream's length, else the container's (WebM gives only that).</summary>
    private static TimeSpan? Duration(AVFormatContext* format, AVStream* stream)
    {
        if (stream is not null && stream->duration > 0 && stream->time_base.den > 0)
            return TimeSpan.FromSeconds(stream->duration * ffmpeg.av_q2d(stream->time_base));
        if (format->duration > 0)
            return TimeSpan.FromSeconds(format->duration / (double)ffmpeg.AV_TIME_BASE);
        return null;
    }

    /// <summary>ReplayGain that libav read into side data, where a format keeps it
    /// outside the tags (an MP3's LAME header).</summary>
    private static double? SideDataGain(AVStream* stream, bool album)
    {
        var side = ffmpeg.av_packet_side_data_get(
            stream->codecpar->coded_side_data, stream->codecpar->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_REPLAYGAIN);
        if (side is null || side->size < 16)
            return null;
        // AVReplayGain, which the bindings leave out: track gain, track peak,
        // album gain, album peak, four 32-bit fields. Gains are in microbels;
        // INT32_MIN is "unknown".
        var fields = (int*)side->data;
        var value = album ? fields[2] : fields[0];
        return value == int.MinValue ? null : value / 100_000.0;
    }

    private static LocalCover? Cover(AVFormatContext* format)
    {
        for (var i = 0; i < format->nb_streams; i++)
        {
            var stream = format->streams[i];
            if ((stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0 || stream->attached_pic.size <= 0)
                continue;
            var extension = stream->codecpar->codec_id switch
            {
                AVCodecID.AV_CODEC_ID_MJPEG => ".jpg",
                AVCodecID.AV_CODEC_ID_PNG => ".png",
                AVCodecID.AV_CODEC_ID_WEBP => ".webp",
                AVCodecID.AV_CODEC_ID_GIF => ".gif",
                AVCodecID.AV_CODEC_ID_BMP => ".bmp",
                _ => null,
            };
            if (extension is null)
                continue;
            return new LocalCover(new ReadOnlySpan<byte>(stream->attached_pic.data, stream->attached_pic.size).ToArray(), extension);
        }
        return null;
    }

    private static string? Get(AVDictionary* tags, string key)
    {
        if (tags is null)
            return null;
        var entry = ffmpeg.av_dict_get(tags, key, null, 0);
        return entry is null ? null : Marshal.PtrToStringUTF8((IntPtr)entry->value);
    }
}
