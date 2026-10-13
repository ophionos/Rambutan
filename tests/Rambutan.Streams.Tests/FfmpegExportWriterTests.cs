using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Rambutan.Core.Downloads;
using Rambutan.Core.Models;

namespace Rambutan.Streams.Tests;

public sealed class FfmpegExportWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));

    public FfmpegExportWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly TrackInfo Hello = new()
    {
        VideoId = "YQHsXMglC9A",
        Title = "Hello",
        Artists = [new ArtistRef("Adele", null)],
        Album = "25",
    };

    private static ExportJob Job(ExportTarget target, string output = "out.ogg") =>
        new(Hello, "in.webm", "opus", "in.jpg", target, output);

    // A 2×3 JPEG's header: SOI, an APP0 segment, then SOF0 with height 3, width 2.
    private static readonly byte[] TinyJpeg =
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,
        0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x03, 0x00, 0x02, 0x01, 0x01, 0x11, 0x00,
        0xFF, 0xD9,
    ];

    [Fact]
    public void Escape_BackslashesWhatFfmetadataReserves()
    {
        Assert.Equal(@"a\=b\;c\#d\\e\" + "\nf", FfmpegExportWriter.Escape("a=b;c#d\\e\r\nf"));
    }

    [Fact]
    public void Metadata_HoldsTitleArtistAlbum_AndSkipsEmptyValues()
    {
        var job = Job(ExportTarget.M4a) with { Track = Hello with { Album = null } };

        Assert.Equal(";FFMETADATA1\ntitle=Hello\nartist=Adele\n", FfmpegExportWriter.Metadata(job, null));
    }

    [Fact]
    public void Metadata_CarriesTheOggCoverAsAPictureBlock()
    {
        var text = FfmpegExportWriter.Metadata(Job(ExportTarget.Ogg), TinyJpeg);

        var line = text.Split('\n').Single(l => l.StartsWith("METADATA_BLOCK_PICTURE=", StringComparison.Ordinal));
        var block = Convert.FromBase64String(line["METADATA_BLOCK_PICTURE=".Length..].Replace("\\=", "="));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(block)); // front cover
        var mimeLength = (int)BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(4));
        Assert.Equal("image/jpeg", Encoding.ASCII.GetString(block, 8, mimeLength));
        var at = 8 + mimeLength + 4;
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(at)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(at + 4)));
        Assert.Equal((uint)TinyJpeg.Length, BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(at + 16)));
        Assert.Equal(TinyJpeg, block[(at + 20)..]);
    }

    [Fact]
    public void ImageInfo_ReadsPngAndJpegSizes_AndRefusesOthers()
    {
        var png = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 544);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 360);

        Assert.Equal(("image/png", 544, 360), FfmpegExportWriter.ImageInfo(png));
        Assert.Equal(("image/jpeg", 2, 3), FfmpegExportWriter.ImageInfo(TinyJpeg));
        Assert.Null(FfmpegExportWriter.ImageInfo("RIFF\0\0\0\0WEBPVP8 "u8));
    }

    [Fact]
    public void Arguments_RemuxOggWithTheCoverInComments()
    {
        var args = FfmpegExportWriter.Arguments(Job(ExportTarget.Ogg), "meta.txt", coverStream: null, "out.part");

        Assert.DoesNotContain("attached_pic", args);
        Assert.Contains("copy", args);
        Assert.DoesNotContain("libmp3lame", args);
        Assert.Equal(["-f", "ogg", "out.part"], args.TakeLast(3));
    }

    [Fact]
    public void Arguments_EncodeMp3WithTheCoverAsAPictureStream()
    {
        var args = FfmpegExportWriter.Arguments(Job(ExportTarget.Mp3, "out.mp3"), "meta.txt", "in.jpg", "out.part");

        Assert.Equal("in.jpg", args[args.ToList().LastIndexOf("-i") + 1]);
        Assert.Contains("attached_pic", args);
        Assert.Contains("libmp3lame", args);
        Assert.Equal(["-f", "mp3", "-id3v2_version", "3", "out.part"], args.TakeLast(5));
    }

    /// <summary>The sidecar's ffmpeg, when this machine has run the app; the
    /// real-ffmpeg tests skip without one (CI has none).</summary>
    private static string? Ffmpeg()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rambutan", "tools", "ffmpeg");
        var path = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        return File.Exists(path) ? path : null;
    }

    private static async Task<string> Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        var error = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, error);
        return output;
    }

    [Theory]
    [InlineData("opus", "libopus", "webm", ".ogg")]
    [InlineData("mp4a.40.2", "aac", "mp4", ".m4a")]
    [InlineData("opus", "libopus", "webm", ".mp3")]
    public async Task RealFfmpeg_WritesTagsAndCover(string codec, string encoder, string container, string extension)
    {
        if (Ffmpeg() is not { } ffmpeg)
        {
            Assert.Skip("No ffmpeg sidecar on this machine.");
            return;
        }
        // The Linux sidecar install keeps only ffmpeg, so ffprobe may be absent.
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (!File.Exists(ffprobe))
        {
            Assert.Skip("No ffprobe beside the ffmpeg sidecar.");
            return;
        }

        var audio = Path.Combine(_dir, "in." + container);
        await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=duration=1",
            "-c:a", encoder, "-metadata:s:a", "language=eng", audio);
        var cover = Path.Combine(_dir, "in.jpg");
        await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:s=64x48",
            "-frames:v", "1", cover);
        var output = Path.Combine(_dir, "Adele - Hello" + extension);
        var target = extension == ".mp3" ? ExportTarget.Mp3 : ExportTarget.For(codec, ExportFormat.Original);

        await new FfmpegExportWriter(_ => Task.FromResult(ffmpeg))
            .WriteAsync(new ExportJob(Hello, audio, codec, cover, target, output), CancellationToken.None);

        var probe = await Run(ffprobe, "-v", "error", "-show_entries",
            "stream=codec_name:stream_disposition=attached_pic:stream_tags:format_tags", "-of", "flat", output);
        Assert.Contains("\"Hello\"", probe);
        Assert.Contains("\"Adele\"", probe);
        Assert.Contains("\"25\"", probe);
        Assert.Contains("codec_name=\"mjpeg\"", probe);
        Assert.Contains("attached_pic=1", probe);
        if (extension != ".m4a") // MP4 has no picture types
            Assert.Contains("Cover (front)", probe);
        Assert.DoesNotContain("eng", probe);
        Assert.Contains(extension == ".mp3" ? "codec_name=\"mp3\"" : extension == ".ogg" ? "codec_name=\"opus\"" : "codec_name=\"aac\"", probe);
        // Only the export is new: no .part file is left beside it.
        Assert.Equal(
            new[] { audio, cover, output }.Select(Path.GetFileName).Order(),
            Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task RealFfmpeg_NeverReplacesAnExistingFile()
    {
        if (Ffmpeg() is not { } ffmpeg)
        {
            Assert.Skip("No ffmpeg sidecar on this machine.");
            return;
        }
        var audio = Path.Combine(_dir, "in.webm");
        await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=duration=1", "-c:a", "libopus", audio);
        var output = Path.Combine(_dir, "taken.ogg");
        await File.WriteAllTextAsync(output, "mine");

        await Assert.ThrowsAsync<IOException>(() => new FfmpegExportWriter(_ => Task.FromResult(ffmpeg))
            .WriteAsync(new ExportJob(Hello, audio, "opus", null, ExportTarget.Ogg, output), CancellationToken.None));

        Assert.Equal("mine", await File.ReadAllTextAsync(output));
        Assert.False(File.Exists(output + ".part"));
    }
}
