using System.Buffers.Binary;
using System.Text;
using Rambutan.Core.Downloads;

namespace Rambutan.Streams;

/// <summary>
/// Writes an exported file with the ffmpeg sidecar: a remux into .ogg or .m4a, or
/// an MP3 encode, with the track's tags and cover embedded. It reads the download's
/// own files only; nothing is fetched.
/// <para>
/// Tags go in through an ffmetadata file rather than the command line: an Ogg
/// cover is a base64 <c>METADATA_BLOCK_PICTURE</c> comment (ffmpeg's Ogg muxer
/// takes no picture stream), easily longer than a Windows command line allows.
/// .m4a and .mp3 take the cover as an attached picture stream.
/// </para>
/// </summary>
public sealed class FfmpegExportWriter(Func<CancellationToken, Task<string>> ffmpegPath) : IExportWriter
{
    private const string PartExtension = ".part";

    public async Task WriteAsync(ExportJob job, CancellationToken ct)
    {
        var ffmpeg = await ffmpegPath(ct);
        var cover = UsableCover(job.ArtworkPath);
        var metadataPath = Path.Combine(Path.GetTempPath(), $"rambutan-export-{Guid.NewGuid():N}.txt");
        var partPath = job.OutputPath + PartExtension;
        try
        {
            var embedInComments = cover is not null && job.Target == ExportTarget.Ogg;
            await File.WriteAllTextAsync(metadataPath,
                Metadata(job, embedInComments ? await File.ReadAllBytesAsync(cover!, ct) : null),
                new UTF8Encoding(false), ct);

            var result = await ProcessRunner.RunAsync(ffmpeg,
                Arguments(job, metadataPath, embedInComments ? null : cover, partPath), ct,
                environment: FfmpegManager.ProcessEnvironment(ffmpeg));
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited with {result.ExitCode}: {LastLine(result.StdErr)}");

            // Never replaces a file that appeared meanwhile: the user's files are theirs.
            File.Move(partPath, job.OutputPath, overwrite: false);
        }
        finally
        {
            TryDelete(partPath);
            TryDelete(metadataPath);
        }
    }

    internal static IReadOnlyList<string> Arguments(ExportJob job, string metadataPath, string? coverStream, string output)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-i", job.AudioPath,
            "-f", "ffmetadata", "-i", metadataPath,
        };
        if (coverStream is not null)
            args.AddRange(["-i", coverStream]);

        // The download's own container tags (language, encoder, durations) don't
        // carry over; only ours do.
        args.AddRange(["-map", "0:a:0", "-map_metadata", "1", "-map_metadata:s:a", "-1"]);
        if (coverStream is not null)
            // The comment sets the ID3 picture type; without it an MP3 cover is "Other".
            args.AddRange(["-map", "2:v:0", "-c:v", "copy", "-disposition:v:0", "attached_pic",
                "-metadata:s:v:0", "comment=Cover (front)"]);

        if (job.Target.Transcodes)
            // LAME V0: the usual "as good as MP3 gets" setting.
            args.AddRange(["-c:a", "libmp3lame", "-q:a", "0"]);
        else
            args.AddRange(["-c:a", "copy"]);

        args.AddRange(job.Target == ExportTarget.Ogg ? ["-f", "ogg"]
            : job.Target == ExportTarget.M4a ? ["-f", "ipod"]
            : ["-f", "mp3", "-id3v2_version", "3"]);
        args.Add(output);
        return args;
    }

    /// <summary>The ffmetadata file: title, artist and album, plus the Ogg cover
    /// as a Vorbis comment when <paramref name="oggCover"/> is given.</summary>
    internal static string Metadata(ExportJob job, byte[]? oggCover)
    {
        var text = new StringBuilder(";FFMETADATA1\n");
        Add("title", job.Track.Title);
        Add("artist", job.Track.ArtistDisplay);
        Add("album", job.Track.Album);
        if (oggCover is not null && PictureBlock(oggCover) is { } block)
            Add("METADATA_BLOCK_PICTURE", Convert.ToBase64String(block));
        return text.ToString();

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                text.Append(key).Append('=').Append(Escape(value)).Append('\n');
        }
    }

    /// <summary>ffmetadata escapes '=', ';', '#', '\' and newlines with a backslash.</summary>
    internal static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '=' or ';' or '#' or '\\' or '\n')
                escaped.Append('\\');
            if (c != '\r')
                escaped.Append(c);
        }
        return escaped.ToString();
    }

    /// <summary>A FLAC picture block (type 3, front cover), as Vorbis comments
    /// carry it; null for an image that is neither JPEG nor PNG.</summary>
    internal static byte[]? PictureBlock(byte[] image)
    {
        if (ImageInfo(image) is not var (mime, width, height))
            return null;
        var mimeBytes = Encoding.ASCII.GetBytes(mime);
        using var block = new MemoryStream(4 + 4 + mimeBytes.Length + 4 + 16 + 4 + image.Length);
        Put(3);
        Put(mimeBytes.Length);
        block.Write(mimeBytes);
        Put(0); // no description
        Put(width);
        Put(height);
        Put(24); // colour depth: the common value; readers take it from the image
        Put(0); // not an indexed image
        Put(image.Length);
        block.Write(image);
        return block.ToArray();

        void Put(int value)
        {
            Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(word, (uint)value);
            block.Write(word);
        }
    }

    /// <summary>The MIME type and size of a JPEG or PNG, from its header; null for
    /// anything else (a .webp cover isn't embedded: few players read one).</summary>
    internal static (string Mime, int Width, int Height)? ImageInfo(ReadOnlySpan<byte> image)
    {
        if (image.Length >= 24 && image.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]))
            return ("image/png",
                (int)BinaryPrimitives.ReadUInt32BigEndian(image[16..]),
                (int)BinaryPrimitives.ReadUInt32BigEndian(image[20..]));

        if (image.Length < 4 || !image.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]))
            return null;
        // Walk the JPEG segments to the first start-of-frame, which holds the size.
        var at = 2;
        while (at + 9 < image.Length)
        {
            if (image[at] != 0xFF)
                return ("image/jpeg", 0, 0);
            var marker = image[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(image[(at + 2)..]);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return ("image/jpeg",
                    BinaryPrimitives.ReadUInt16BigEndian(image[(at + 7)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(image[(at + 5)..]));
            at += 2 + length;
        }
        return ("image/jpeg", 0, 0);
    }

    private static string? UsableCover(string? path)
    {
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            Span<byte> header = stackalloc byte[24];
            using var file = File.OpenRead(path);
            var read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            var head = header[..read];
            return head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]) ||
                   head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47])
                ? path
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } lines
            ? lines[^1]
            : "no output";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
