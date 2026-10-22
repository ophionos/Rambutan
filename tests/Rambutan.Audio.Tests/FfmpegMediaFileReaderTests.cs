using Rambutan.Core.Local;

namespace Rambutan.Audio.Tests;

/// <summary>
/// The reader against small generated files (a second of tone, made-up tags).
/// It needs the ffmpeg shared libraries: <c>RAMBUTAN_FFMPEG_LIBS</c> names their
/// folder, else the app's own tools folder on Windows and the system's libraries
/// on Linux. Without them these tests are skipped.
/// </summary>
public class FfmpegMediaFileReaderTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Local");
    private readonly FfmpegMediaFileReader _reader = new();

    public FfmpegMediaFileReaderTests() => Assert.SkipUnless(Libav.Available, "Needs the ffmpeg shared libraries.");

    private LocalFileTags Read(string name) =>
        _reader.Read(Path.Combine(Fixtures, name)) ?? throw new Xunit.Sdk.XunitException($"{name} wasn't read");

    [Theory]
    [InlineData("tags.mp3", "mp3")]
    [InlineData("tags.flac", "flac")]
    [InlineData("tags.opus", "opus")]
    [InlineData("tags.m4a", "aac")]
    [InlineData("tags.webm", "opus")]
    public void ReadsTheTagsAndLength(string name, string codec)
    {
        var tags = Read(name);

        Assert.Equal(codec, tags.Codec);
        Assert.Equal("Testlied", tags.Title);
        Assert.Equal("Testband", tags.Artist);
        Assert.Equal("Testalbum", tags.Album);
        Assert.Equal("Various", tags.AlbumArtist);
        Assert.Equal("Ambient", tags.Genre);
        Assert.Equal(3, tags.TrackNumber);
        Assert.Equal(2019, tags.Year);
        Assert.InRange(tags.Duration!.Value.TotalSeconds, 0.9, 1.2);
    }

    [Theory]
    [InlineData("tags.mp3", ".png")]
    [InlineData("tags.flac", ".png")]
    public void ReadsTheEmbeddedCover(string name, string extension)
    {
        var cover = Read(name).Cover;

        Assert.NotNull(cover);
        Assert.Equal(extension, cover.Extension);
        Assert.Equal((byte)0x89, cover.Data[0]); // PNG signature
    }

    [Fact]
    public void ReadsReplayGain()
    {
        var mp3 = Read("tags.mp3");

        Assert.Equal(-6.2, mp3.TrackGainDb!.Value, 2);
        Assert.Equal(-5.1, mp3.AlbumGainDb!.Value, 2);
        Assert.Equal(-3.0, Read("tags.flac").TrackGainDb!.Value, 2);
        // R128 −5 dB to −23 LUFS is 0 dB to ReplayGain's −18.
        Assert.Equal(0.0, Read("tags.opus").TrackGainDb!.Value, 2);
    }

    [Theory]
    [InlineData("lyrics.mp3", "[00:00.20]Erste Zeile")] // ID3v2 USLT, read as lyrics-eng
    [InlineData("lyrics.m4a", "[00:00.20]Erste Zeile")] // MP4 ©lyr
    [InlineData("lyrics.flac", "Erste Zeile")]          // Vorbis LYRICS, beside a LYRICIST
    [InlineData("lyrics.opus", "Erste Zeile")]          // Vorbis UNSYNCEDLYRICS, on the stream
    public void ReadsTheLyricsTags_AndNothingElse(string name, string start)
    {
        var lyrics = _reader.ReadLyrics(Path.Combine(Fixtures, name));

        Assert.StartsWith(start, Assert.Single(lyrics));
    }

    [Fact]
    public void AFileWithoutLyrics_HasNone()
    {
        Assert.Empty(_reader.ReadLyrics(Path.Combine(Fixtures, "tags.flac")));
        Assert.Empty(_reader.ReadLyrics(Path.Combine(Fixtures, "gone.mp3")));
    }

    [Fact]
    public void AMisnamedFile_IsReadAsWhatItIs()
    {
        var tags = Read("flac-named.mp3");

        Assert.Equal("flac", tags.Codec);
        Assert.Equal("Testlied", tags.Title);
        Assert.InRange(tags.Duration!.Value.TotalSeconds, 0.9, 1.2);
    }

    [Fact]
    public void AFileThatIsNotAudio_IsNull() =>
        Assert.Null(_reader.Read(Path.Combine(Fixtures, "not-audio.mp3")));

    [Fact]
    public void AFileThatIsGone_IsNull() =>
        Assert.Null(_reader.Read(Path.Combine(Fixtures, "gone.mp3")));

    [Fact]
    public void APathOutsideAscii_IsRead()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"rambutan-Ünïcødé 音楽 {Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "Läuft – 曲.mp3");
            File.Copy(Path.Combine(Fixtures, "tags.mp3"), path);

            Assert.Equal("Testlied", _reader.Read(path)?.Title);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static class Libav
    {
        public static bool Available { get; } = TryInitialize();

        private static bool TryInitialize()
        {
            var folder = Environment.GetEnvironmentVariable("RAMBUTAN_FFMPEG_LIBS");
            if (folder is null && OperatingSystem.IsWindows())
            {
                folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rambutan", "tools", "ffmpeg");
                if (!Directory.Exists(folder))
                    return false;
            }
            try
            {
                FfmpegRuntime.Initialize(folder);
                FfmpegRuntime.Version();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

/// <summary>The ID3v2 skip, which needs no libav.</summary>
public class Id3v2LengthTests
{
    [Fact]
    public void IncludesTheHeader()
    {
        // Size 0x01 0x00 in synchsafe bytes is 128.
        byte[] tag = [(byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 1, 0];

        Assert.Equal(138, FfmpegMediaFileReader.Id3v2Length(new MemoryStream(tag)));
    }

    [Fact]
    public void AddsTheFooter_WhenTheFlagSaysThereIsOne()
    {
        byte[] tag = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0x10, 0, 0, 1, 0];

        Assert.Equal(148, FfmpegMediaFileReader.Id3v2Length(new MemoryStream(tag)));
    }

    [Fact]
    public void IsZero_WithoutATag() =>
        Assert.Equal(0, FfmpegMediaFileReader.Id3v2Length(new MemoryStream("fLaC"u8.ToArray())));
}
